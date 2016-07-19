﻿using Google.Apis.Calendar.v3.Data;
using log4net;
using Microsoft.Office.Interop.Outlook;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OutlookGoogleCalendarSync {
    /// <summary>
    /// Description of MainForm.
    /// </summary>
    public partial class MainForm : Form {
        public static MainForm Instance;
        public NotificationTray NotificationTray { get; set; }
        public ToolTip ToolTips;

        public SyncTimer OgcsTimer;
        private AbortableBackgroundWorker bwSync;
        public Boolean SyncingNow {
            get {
                if (bwSync == null) return false;
                else return bwSync.IsBusy;
            }
        }
        public Boolean ManualForceCompare = false;
        private static readonly ILog log = LogManager.GetLogger(typeof(MainForm));
        private Rectangle tabAppSettings_background = new Rectangle();
        private float magnification = Graphics.FromHwnd(IntPtr.Zero).DpiY / 96; //Windows Display Magnifier (96DPI = 100%)

        public MainForm(string startingTab = null) {
            log.Debug("Initialiasing MainForm.");
            InitializeComponent();

            if (startingTab != null && startingTab == "Help") this.tabApp.SelectedTab = this.tabPage_Help;

            Instance = this;

            Social.TrackVersion();
            updateGUIsettings();
            Settings.Instance.LogSettings();
            NotificationTray = new NotificationTray(this.trayIcon);
            
            log.Debug("Create the timer for the auto synchronisation");
            OgcsTimer = new SyncTimer();

            //Set up listener for Outlook calendar changes
            if (Settings.Instance.OutlookPush) OutlookCalendar.Instance.RegisterForPushSync();

            if (Settings.Instance.StartInTray) {
                this.CreateHandle();
                this.WindowState = FormWindowState.Minimized;
            }
        }

        private void updateGUIsettings() {
            this.SuspendLayout();
            #region Tooltips
            //set up tooltips for some controls
            ToolTips = new ToolTip();
            ToolTips.AutoPopDelay = 10000;
            ToolTips.InitialDelay = 500;
            ToolTips.ReshowDelay = 200;
            ToolTips.ShowAlways = true;
            //Outlook
            ToolTips.SetToolTip(cbOutlookCalendars,
                "The Outlook calendar to synchonize with.");
            ToolTips.SetToolTip(btTestOutlookFilter,
                "Check how many appointments are returned for the date range being synced.");
            //Google
            ToolTips.SetToolTip(cbGoogleCalendars,
                "The Google calendar to synchonize with.");
            ToolTips.SetToolTip(btResetGCal,
                "Reset the Google account being used to synchonize with.");
            //Settings
            ToolTips.SetToolTip(tbInterval,
                "Set to zero to disable");
            ToolTips.SetToolTip(rbOutlookAltMB,
                "Only choose this if you need to use an Outlook Calendar that is not in the default mailbox");
            ToolTips.SetToolTip(cbMergeItems,
                "If the destination calendar has pre-existing items, don't delete them");
            ToolTips.SetToolTip(cbOutlookPush,
                "Synchronise changes in Outlook to Google within a few minutes.");
            ToolTips.SetToolTip(cbOfuscate,
                "Mask specified words in calendar item subject.\nTakes effect for new or updated calendar items.");
            ToolTips.SetToolTip(dgObfuscateRegex,
                "All rules are applied using AND logic");
            ToolTips.SetToolTip(cbUseGoogleDefaultReminder,
                "If the calendar settings in Google have a default reminder configured, use this when Outlook has no reminder.");
            ToolTips.SetToolTip(cbReminderDND,
                "Do Not Disturb: Don't sync reminders to Google if they will trigger between these times.");
            //Application behaviour
            ToolTips.SetToolTip(cbPortable,
                "For ZIP deployments, store configuration files in the application folder (useful if running from a USB thumb drive).\n" +
                "Default is in your User roaming profile.");
            ToolTips.SetToolTip(cbCreateFiles,
                "If checked, all entries found in Outlook/Google and identified for creation/deletion will be exported \n" +
                "to CSV files in the application's directory (named \"*.csv\"). \n" +
                "Only for debug/diagnostic purposes.");
            ToolTips.SetToolTip(rbProxyIE,
                "If IE settings have been changed, a restart of the Sync application may be required");
            #endregion

            cbVerboseOutput.Checked = Settings.Instance.VerboseOutput;
            #region Outlook box
            gbEWS.Enabled = false;
            #region Mailbox
            if (Settings.Instance.OutlookService == OutlookCalendar.Service.AlternativeMailbox) {
                rbOutlookAltMB.Checked = true;
            } else if (Settings.Instance.OutlookService == OutlookCalendar.Service.EWS) {
                rbOutlookEWS.Checked = true;
                gbEWS.Enabled = true;
            } else {
                rbOutlookDefaultMB.Checked = true;
                ddMailboxName.Enabled = false;
            }
            txtEWSPass.Text = Settings.Instance.EWSpassword;
            txtEWSUser.Text = Settings.Instance.EWSuser;
            txtEWSServerURL.Text = Settings.Instance.EWSserver;

            //Mailboxes the user has access to
            log.Debug("Find Accounts");
            if (OutlookCalendar.Instance.Accounts.Count == 1) {
                rbOutlookAltMB.Enabled = false;
                rbOutlookAltMB.Checked = false;
                ddMailboxName.Enabled = false;
            }
            for (int acc = 1; acc <= OutlookCalendar.Instance.Accounts.Count - 1; acc++) {
                String mailbox = OutlookCalendar.Instance.Accounts[acc];
                ddMailboxName.Items.Add(mailbox);
                if (Settings.Instance.MailboxName == mailbox) { ddMailboxName.SelectedIndex = acc - 1; }
            }
            if (ddMailboxName.SelectedIndex == -1 && ddMailboxName.Items.Count > 0) { ddMailboxName.SelectedIndex = 0; }

            log.Debug("List Calendar folders");
            cbOutlookCalendars.SelectedIndexChanged -= cbOutlookCalendar_SelectedIndexChanged;
            cbOutlookCalendars.DataSource = new BindingSource(OutlookCalendar.Instance.CalendarFolders, null);
            cbOutlookCalendars.DisplayMember = "Key";
            cbOutlookCalendars.ValueMember = "Value";
            cbOutlookCalendars.SelectedIndex = -1; //Reset to nothing selected
            cbOutlookCalendars.SelectedIndexChanged += cbOutlookCalendar_SelectedIndexChanged;
            //Select the right calendar
            int c = 0;
            foreach (KeyValuePair<String, MAPIFolder> calendarFolder in OutlookCalendar.Instance.CalendarFolders) {
                if (calendarFolder.Value.EntryID == Settings.Instance.UseOutlookCalendar.Id) {
                    cbOutlookCalendars.SelectedIndex = c;
                }
                c++;
            }
            if (cbOutlookCalendars.SelectedIndex == -1) cbOutlookCalendars.SelectedIndex = 0;
            #endregion
            #region Categories
            cbCategoryFilter.SelectedItem = Settings.Instance.CategoriesRestrictBy == Settings.RestrictBy.Include ?
                "Include" : "Exclude";
            clbCategories.Items.Clear();
            if (OutlookFactory.OutlookVersion < 12) {
                cbCategoryFilter.Enabled = false;
                clbCategories.Enabled = false;
                lFilterCategories.Enabled = false;
            } else {
                foreach (Category cat in OutlookCalendar.Instance.IOutlook.GetCategories() as Categories) {
                    clbCategories.Items.Add(cat.Name);
                }
                foreach (String cat in Settings.Instance.Categories) {
                    try {
                        clbCategories.SetItemChecked(clbCategories.Items.IndexOf(cat), true);
                    } catch { /* Category "cat" no longer exists */ }
                }
            }
            #endregion
            #region DateTime Format / Locale
            Dictionary<string, string> customDates = new Dictionary<string, string>();
            customDates.Add("Default", "g");
            String shortDate = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
            //Outlook can't handle dates or times formatted with a . delimeter!
            switch (shortDate) {
                case "yyyy.MMdd": shortDate = "yyyy-MM-dd"; break;
                default: break;
            }
            String shortTime = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Replace(".", ":");
            customDates.Add("Short Date & Time", shortDate + " " + shortTime);
            customDates.Add("Full (Short Time)", "f");
            customDates.Add("Full Month", "MMMM dd, yyyy hh:mm tt");
            customDates.Add("Generic", "yyyy-MM-dd hh:mm tt");
            customDates.Add("Custom", "yyyy-MM-dd hh:mm tt");
            cbOutlookDateFormat.DataSource = new BindingSource(customDates, null);
            cbOutlookDateFormat.DisplayMember = "Key";
            cbOutlookDateFormat.ValueMember = "Value";
            for (int i = 0; i < cbOutlookDateFormat.Items.Count; i++) {
                KeyValuePair<string, string> aFormat = (KeyValuePair<string, string>)cbOutlookDateFormat.Items[i];
                if (aFormat.Value == Settings.Instance.OutlookDateFormat) {
                    cbOutlookDateFormat.SelectedIndex = i;
                    break;
                } else if (i == cbOutlookDateFormat.Items.Count - 1 && cbOutlookDateFormat.SelectedIndex == 0) {
                    cbOutlookDateFormat.SelectedIndex = i;
                    tbOutlookDateFormat.Text = Settings.Instance.OutlookDateFormat;
                    tbOutlookDateFormat.ReadOnly = false;
                }
            }
            #endregion
            #endregion
            #region Google box
            if (Settings.Instance.UseGoogleCalendar != null && Settings.Instance.UseGoogleCalendar.Id != null) {
                cbGoogleCalendars.Items.Add(Settings.Instance.UseGoogleCalendar);
                cbGoogleCalendars.SelectedIndex = 0;
            }
            #endregion
            #region Sync Options box
            #region How
            this.gbSyncOptions_How.Height = Convert.ToInt16(109 * magnification);
            syncDirection.Items.Add(SyncDirection.OutlookToGoogle);
            syncDirection.Items.Add(SyncDirection.GoogleToOutlook);
            syncDirection.Items.Add(SyncDirection.Bidirectional);
            syncDirection.Items.Add(SyncDirection.OutlookToGoogleSimple);
            cbObfuscateDirection.Items.Add(SyncDirection.OutlookToGoogle);
            cbObfuscateDirection.Items.Add(SyncDirection.GoogleToOutlook);
            cbObfuscateDirection.Items.Add(SyncDirection.OutlookToGoogleSimple);
            //Sync Direction dropdown
            for (int i = 0; i < syncDirection.Items.Count; i++) {
                SyncDirection sd = (syncDirection.Items[i] as SyncDirection);
                if (sd.Id == Settings.Instance.SyncDirection.Id) {
                    syncDirection.SelectedIndex = i;
                }
            }
            if (syncDirection.SelectedIndex == -1) syncDirection.SelectedIndex = 0;
            this.gbSyncOptions_How.SuspendLayout();
            cbMergeItems.Checked = Settings.Instance.MergeItems;
            cbDisableDeletion.Checked = Settings.Instance.DisableDelete;
            cbConfirmOnDelete.Enabled = !Settings.Instance.DisableDelete;
            cbConfirmOnDelete.Checked = Settings.Instance.ConfirmOnDelete;
            cbOfuscate.Checked = Settings.Instance.Obfuscation.Enabled;
            //Obfuscate Direction dropdown
            for (int i = 0; i < cbObfuscateDirection.Items.Count; i++) {
                SyncDirection sd = (cbObfuscateDirection.Items[i] as SyncDirection);
                if (sd.Id == Settings.Instance.Obfuscation.Direction.Id) {
                    cbObfuscateDirection.SelectedIndex = i;
                }
            }
            if (cbObfuscateDirection.SelectedIndex == -1) cbObfuscateDirection.SelectedIndex = 0;
            cbObfuscateDirection.Enabled = Settings.Instance.SyncDirection == SyncDirection.Bidirectional;
            Settings.Instance.Obfuscation.LoadRegex(dgObfuscateRegex);
            this.gbSyncOptions_How.ResumeLayout();
            #endregion
            #region When
            this.gbSyncOptions_When.SuspendLayout();
            tbDaysInThePast.Text = Settings.Instance.DaysInThePast.ToString();
            tbDaysInTheFuture.Text = Settings.Instance.DaysInTheFuture.ToString();
            tbInterval.ValueChanged -= new System.EventHandler(this.tbMinuteOffsets_ValueChanged);
            tbInterval.Value = Settings.Instance.SyncInterval;
            tbInterval.ValueChanged += new System.EventHandler(this.tbMinuteOffsets_ValueChanged);
            cbIntervalUnit.SelectedIndexChanged -= new System.EventHandler(this.cbIntervalUnit_SelectedIndexChanged);
            cbIntervalUnit.Text = Settings.Instance.SyncIntervalUnit;
            cbIntervalUnit.SelectedIndexChanged += new System.EventHandler(this.cbIntervalUnit_SelectedIndexChanged);
            cbOutlookPush.Checked = Settings.Instance.OutlookPush;
            this.gbSyncOptions_When.ResumeLayout();
            #endregion
            #region What
            this.gbSyncOptions_What.SuspendLayout();
            cbAddDescription.Checked = Settings.Instance.AddDescription;
            cbAddDescription_OnlyToGoogle.Checked = Settings.Instance.AddDescription_OnlyToGoogle;
            cbAddAttendees.Checked = Settings.Instance.AddAttendees;
            cbAddReminders.Checked = Settings.Instance.AddReminders;
            cbUseGoogleDefaultReminder.Checked = Settings.Instance.UseGoogleDefaultReminder;
            cbUseGoogleDefaultReminder.Enabled = Settings.Instance.AddReminders;
            cbReminderDND.Enabled = Settings.Instance.AddReminders;
            cbReminderDND.Checked = Settings.Instance.ReminderDND;
            dtDNDstart.Enabled = Settings.Instance.AddReminders;
            dtDNDend.Enabled = Settings.Instance.AddReminders;
            dtDNDstart.Value = Settings.Instance.ReminderDNDstart;
            dtDNDend.Value = Settings.Instance.ReminderDNDend;

            this.gbSyncOptions_What.ResumeLayout();
            #endregion
            #endregion
            #region Application behaviour
            cbShowBubbleTooltips.Checked = Settings.Instance.ShowBubbleTooltipWhenSyncing;
            cbStartOnStartup.Checked = Settings.Instance.StartOnStartup;
            cbStartInTray.Checked = Settings.Instance.StartInTray;
            cbMinimiseToTray.Checked = Settings.Instance.MinimiseToTray;
            cbMinimiseNotClose.Checked = Settings.Instance.MinimiseNotClose;
            cbPortable.Checked = Settings.Instance.Portable;
            cbPortable.Enabled = !Program.isClickOnceInstall();
            cbCreateFiles.Checked = Settings.Instance.CreateCSVFiles;
            for (int i = 0; i < cbLoggingLevel.Items.Count; i++) {
                if (cbLoggingLevel.Items[i].ToString().ToLower() == Settings.Instance.LoggingLevel.ToLower()) {
                    cbLoggingLevel.SelectedIndex = i;
                    break;
                }
            }
            updateGUIsettings_Proxy();
            #endregion
            #region Dev Options
            cbGoogleDevOverride.Checked = Settings.Instance.OverrideDeveloper;
            txtGoogleDevClientID.Text = Settings.Instance.GoogleDeveloperClientID;
            txtGoogleDevClientSecret.Text = Settings.Instance.GoogleDeveloperClientSecret;
            cbEnableAutoRetry.Checked = Settings.Instance.EnableAutoRetry;
            tbAutoRetryDelay.Value = Settings.Instance.AutoRetryDelayMin;
            #endregion
            #region About
            int r = 0;
            dgAbout.Rows.Add();
            dgAbout.Rows[r].Cells[0].Value = "Version";
            dgAbout.Rows[r].Cells[1].Value = System.Windows.Forms.Application.ProductVersion;
            dgAbout.Rows.Add(); r++;
            dgAbout.Rows[r].Cells[0].Value = "Running From";
            dgAbout.Rows[r].Cells[1].Value = System.Windows.Forms.Application.ExecutablePath;
            dgAbout.Rows.Add(); r++;
            dgAbout.Rows[r].Cells[0].Value = "Config In";
            dgAbout.Rows[r].Cells[1].Value = Program.UserFilePath;
            dgAbout.Rows.Add(); r++;
            dgAbout.Rows[r].Cells[0].Value = "Subscription";
            dgAbout.Rows[r].Cells[1].Value = (Settings.Instance.Subscribed == DateTime.Parse("01-Jan-2000")) ? "N/A" : Settings.Instance.Subscribed.ToShortDateString();
            dgAbout.Height = (dgAbout.Rows[r].Height * 4) + 2;
            
            cbAlphaReleases.Checked = Settings.Instance.AlphaReleases;
            cbAlphaReleases.Visible = !Program.isClickOnceInstall();
            #endregion
            this.ResumeLayout();
        }

        private void updateGUIsettings_Proxy() {
            rbProxyIE.Checked = true;
            rbProxyNone.Checked = (Settings.Instance.Proxy.Type == "None");
            rbProxyCustom.Checked = (Settings.Instance.Proxy.Type == "Custom");
            cbProxyAuthRequired.Enabled = (Settings.Instance.Proxy.Type == "Custom");
            txtProxyServer.Text = Settings.Instance.Proxy.ServerName;
            txtProxyPort.Text = Settings.Instance.Proxy.Port.ToString();
            txtProxyServer.Enabled = rbProxyCustom.Checked;
            txtProxyPort.Enabled = rbProxyCustom.Checked;

            if (!string.IsNullOrEmpty(Settings.Instance.Proxy.UserName) &&
                !string.IsNullOrEmpty(Settings.Instance.Proxy.Password)) {
                cbProxyAuthRequired.Checked = true;
            } else {
                cbProxyAuthRequired.Checked = false;
            }
            txtProxyUser.Text = Settings.Instance.Proxy.UserName;
            txtProxyPassword.Text = Settings.Instance.Proxy.Password;
            txtProxyUser.Enabled = cbProxyAuthRequired.Checked;
            txtProxyPassword.Enabled = cbProxyAuthRequired.Checked;
        }
        
        private void applyProxy() {
            if (rbProxyNone.Checked) Settings.Instance.Proxy.Type = rbProxyNone.Tag.ToString();
            else if (rbProxyCustom.Checked) Settings.Instance.Proxy.Type = rbProxyCustom.Tag.ToString();
            else Settings.Instance.Proxy.Type = rbProxyIE.Tag.ToString();
            
            if (rbProxyCustom.Checked) {
                if (String.IsNullOrEmpty(txtProxyServer.Text) || String.IsNullOrEmpty(txtProxyPort.Text)) {
                    MessageBox.Show("A proxy server name and port must be provided.", "Proxy Authentication Enabled",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    txtProxyServer.Focus();
                    return;
                }
                int nPort;
                if (!int.TryParse(txtProxyPort.Text, out nPort)) {
                    MessageBox.Show("Proxy server port must be a number.", "Invalid Proxy Port",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    txtProxyPort.Focus();
                    return;
                }

                string userName = null;
                string password = null;
                if (cbProxyAuthRequired.Checked) {
                    userName = txtProxyUser.Text;
                    password = txtProxyPassword.Text;
                } else {
                    userName = string.Empty;
                    password = string.Empty;
                }

                Settings.Instance.Proxy.ServerName = txtProxyServer.Text;
                Settings.Instance.Proxy.Port = nPort;
                Settings.Instance.Proxy.UserName = userName;
                Settings.Instance.Proxy.Password = password;
            }
            Settings.Instance.Proxy.Configure();
        }

        public void Sync_Click(object sender, EventArgs e) {
            try {
                Sync_Requested(sender, e);
            } catch (System.Exception ex) {
                MainForm.Instance.Logboxout("WARNING: Problem encountered during synchronisation.\r\n" + ex.Message);
                log.Error(ex.StackTrace);
            }
        }
        public void Sync_Requested(object sender = null, EventArgs e = null) {
            ManualForceCompare = false;
            if (sender != null && sender.GetType().ToString().EndsWith("Timer")) { //Automated sync
                NotificationTray.UpdateItem("delayRemove", enabled: false);
                if (bSyncNow.Text == "Start Sync") {
                    log.Info("Scheduled sync started.");
                    Timer aTimer = sender as Timer;
                    if (aTimer.Tag.ToString() == "PushTimer") sync_Start(updateSyncSchedule: false);
                    else if (aTimer.Tag.ToString() == "AutoSyncTimer") sync_Start(updateSyncSchedule: true);
                } else if (bSyncNow.Text == "Stop Sync") {
                    log.Warn("Automated sync triggered whilst previous sync is still running. Ignoring this new request.");
                    if (bwSync == null)
                        log.Debug("Background worker is null somehow?!");
                    else
                        log.Debug("Background worker is busy? A:" + bwSync.IsBusy.ToString());
                }

            } else { //Manual sync
                if (bSyncNow.Text == "Start Sync") {
                    log.Info("Manual sync started.");
                    if (Control.ModifierKeys == Keys.Shift) { ManualForceCompare = true; log.Info("Shift-click has forced a compare of all items"); }
                    sync_Start(updateSyncSchedule: false);

                } else if (bSyncNow.Text == "Stop Sync") {
                    if (bwSync != null && !bwSync.CancellationPending) {
                        log.Warn("Sync cancellation requested.");
                        bwSync.CancelAsync();
                    } else {
                        Logboxout("Repeated cancellation requested - forcefully aborting thread!");
                        try {
                            bwSync.Abort();
                            bwSync.Dispose();
                            bwSync = null;
                        } catch { }
                    }
                }
            }
        }

        private void sync_Start(Boolean updateSyncSchedule = true) {
            LogBox.Clear();

            if (Settings.Instance.UseGoogleCalendar == null ||
                Settings.Instance.UseGoogleCalendar.Id == null ||
                Settings.Instance.UseGoogleCalendar.Id == "") {
                MessageBox.Show("You need to select a Google Calendar first on the 'Settings' tab.");
                return;
            }
            //Check network availability
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) {
                Logboxout("There does not appear to be any network available! Sync aborted.", notifyBubble: true);
                return;
            }
            //Check if Outlook is Online
            try {
                if (OutlookCalendar.Instance.IOutlook.Offline() && Settings.Instance.AddAttendees) {
                    Logboxout("You have selected to sync attendees but Outlook is currently offline.");
                    Logboxout("Either put Outlook online or do not sync attendees.", notifyBubble: true);
                    return;
                }
            } catch (System.Exception ex) {
                Logboxout(ex.Message, notifyBubble: true);
                log.Error(ex.StackTrace);
                return;
            }
            GoogleCalendar.APIlimitReached_attendee = false;
            MainForm.Instance.syncNote(SyncNotes.QuotaExhaustedInfo, null, false);
            bSyncNow.Text = "Stop Sync";
            NotificationTray.UpdateItem("sync", "&Stop Sync");

            String cacheNextSync = lNextSyncVal.Text;
            lNextSyncVal.Text = "In progress...";

            DateTime SyncStarted = DateTime.Now;
            log.Info("Sync version: " + System.Windows.Forms.Application.ProductVersion);
            Logboxout("Sync started at " + SyncStarted.ToString());
            Logboxout("Syncing from " + Settings.Instance.SyncStart.ToShortDateString() +
                " to " + Settings.Instance.SyncEnd.ToShortDateString());
            Logboxout(Settings.Instance.SyncDirection.Name);
            Logboxout("--------------------------------------------------");

            if (Settings.Instance.OutlookPush) OutlookCalendar.Instance.DeregisterForPushSync();

            Boolean syncOk = false;
            int failedAttempts = 0;
            Social.TrackSync();
            GoogleCalendar.Instance.GetCalendarSettings();
            while (!syncOk) {

                if (failedAttempts > 0 && Settings.Instance.EnableAutoRetry)
                {
                    bSyncNow.Text = "Start Sync";
                    NotificationTray.UpdateItem("sync", "&Sync Now");
                    break;
                }

                if (failedAttempts > 0 &&
                    MessageBox.Show("The synchronisation failed - check the Sync tab for further details.\r\nDo you want to try again?", "Sync Failed",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == System.Windows.Forms.DialogResult.No) 
                {
                    bSyncNow.Text = "Start Sync";
                    NotificationTray.UpdateItem("sync", "&Sync Now");
                    break;
                }

                //Set up a separate thread for the sync to operate in. Keeps the UI responsive.
                bwSync = new AbortableBackgroundWorker();
                //Don't need thread to report back. The logbox is updated from the thread anyway.
                bwSync.WorkerReportsProgress = false;
                bwSync.WorkerSupportsCancellation = true;

                //Kick off the sync in the background thread
                bwSync.DoWork += new DoWorkEventHandler(
                    delegate(object o, DoWorkEventArgs args) {
                        BackgroundWorker b = o as BackgroundWorker;
                        try {
                            syncOk = synchronize();
                        } catch (System.Exception ex) {
                            MainForm.Instance.Logboxout("The following error was encountered during sync:-");
                            MainForm.Instance.Logboxout(ex.Message, notifyBubble: true);
                            log.Error(ex.StackTrace);
                            syncOk = false;
                        }
                    }
                );

                bwSync.RunWorkerAsync();
                while (bwSync != null && (bwSync.IsBusy || bwSync.CancellationPending)) {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
                try {
                    //Get Logbox text - this is a little bit dirty!
                    if (!syncOk && LogBox.Text.Contains("The RPC server is unavailable.")) {
                        Logboxout("Attempting to reconnect to Outlook...");
                        try { OutlookCalendar.Instance.Reset(); } catch { }
                    }
                } finally {
                    failedAttempts += !syncOk ? 1 : 0;
                }
            }
            Settings.Instance.CompletedSyncs += syncOk ? 1 : 0;
            bSyncNow.Text = "Start Sync";
            NotificationTray.UpdateItem("sync", "&Sync Now");

            Logboxout(syncOk ? "Sync finished with success!" : "Operation aborted after " + failedAttempts + " failed attempts!");

            if (Settings.Instance.OutlookPush) OutlookCalendar.Instance.RegisterForPushSync();

            lLastSyncVal.Text = SyncStarted.ToLongDateString() + " - " + SyncStarted.ToLongTimeString();
            Settings.Instance.LastSyncDate = SyncStarted;
            if (!updateSyncSchedule) {
                lNextSyncVal.Text = cacheNextSync;
            } else {
                if (syncOk) {
                    OgcsTimer.LastSyncDate = SyncStarted;
                    OgcsTimer.SetNextSync();
                } else {

                    if (Settings.Instance.AutoRetryDelayMin != 0) {
                        Logboxout("Another sync has been scheduled to automatically run in " + Settings.Instance.AutoRetryDelayMin + " minutes time.");
                        OgcsTimer.SetNextSync(Settings.Instance.AutoRetryDelayMin, fromNow: true);
                        goto SkipSettingFor5Minutes;
                    }

                    if (Settings.Instance.SyncInterval != 0) {
                        Logboxout("Another sync has been scheduled to automatically run in 5 minutes time.");
                        OgcsTimer.SetNextSync(5, fromNow: true);
                    }

                SkipSettingFor5Minutes:
                    log.Debug("Skipped the 5 minute reschedule");

                }
            }
            bSyncNow.Enabled = true;
            if (OutlookCalendar.Instance.OgcsPushTimer != null)
                OutlookCalendar.Instance.OgcsPushTimer.ItemsQueued = 0; //Reset Push flag regardless of success (don't want it trying every 2 mins)

            checkSyncMilestone();
        }

        private Boolean synchronize() {
            #region Read Outlook items
            Logboxout("Reading Outlook Calendar Entries...");
            List<AppointmentItem> outlookEntries = null;
            try {

                if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogleSimple) {
                    outlookEntries = OutlookCalendar.Instance.GetCalendarEntriesInRange(includeRecurrences: true);
                    goto SkipGettingOutlookEntries;
                }
                
                outlookEntries = OutlookCalendar.Instance.GetCalendarEntriesInRange();

            SkipGettingOutlookEntries:
                log.Debug("Got all Outlook entries in range including recurring entries");

            } catch (System.Exception ex) {
                Logboxout("Unable to access the Outlook calendar.");
                throw ex;
            }
            Logboxout(outlookEntries.Count + " Outlook calendar entries found.");
            Logboxout("--------------------------------------------------");

            log.Debug(outlookEntries.Count + " Outlook calendar entries found.");
            for (int i = outlookEntries.Count - 1; i >= 0; i--) {
                log.Debug("Found " + i + "/" + outlookEntries.Count + ": " + OutlookCalendar.GetEventSummary(outlookEntries[i]));
            }

            #endregion

            #region Read Google items
            Logboxout("Reading Google Calendar Entries...");
            List<Event> googleEntries = null;
            try {

                if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogleSimple) {
                    googleEntries = GoogleCalendar.Instance.GetCalendarEntriesInRange(Settings.Instance.SyncStart.AddMonths(-36),
                                                                                  Settings.Instance.SyncEnd.AddMonths(36));
                    for (int i = googleEntries.Count - 1; i >= 0; i--)
                    {
                        if (googleEntries[i].Status == "cancelled")
                            googleEntries.RemoveAt(i);
                    }
                    goto SkipGettingGoogleEntries;
                }
                    
                googleEntries = GoogleCalendar.Instance.GetCalendarEntriesInRange();

            SkipGettingGoogleEntries:
                log.Debug("Got +/-3 year range of Google entries");

            } catch (DotNetOpenAuth.Messaging.ProtocolException ex) {

                if (Settings.Instance.EnableAutoRetry)
                    goto SkipIECheck;

                Logboxout("ERROR: Unable to connect to the Google calendar.");
                if (MessageBox.Show("Please ensure you can access the internet with Internet Explorer.\r\n" +
                    "Test it now? If successful, please retry synchronising your calendar.",
                    "Test Internet Access",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                    System.Diagnostics.Process.Start("iexplore.exe", "http://www.google.com");
                }

            SkipIECheck:

                throw ex;
            } catch (System.Exception ex) {
                Logboxout("ERROR: Unable to connect to the Google calendar.");
                throw ex;
            }
            Logboxout(googleEntries.Count + " Google calendar entries found.");

            log.Debug(googleEntries.Count + " Google calendar entries found.");
            for (int i = googleEntries.Count - 1; i >= 0; i--) {
                log.Debug("Found " + i + "/" + googleEntries.Count + ": " + GoogleCalendar.GetEventSummary(googleEntries[i]));
            }

            Recurrence.Instance.SeparateGoogleExceptions(googleEntries);
            if (Recurrence.Instance.GoogleExceptions != null && Recurrence.Instance.GoogleExceptions.Count > 0)
                Logboxout(Recurrence.Instance.GoogleExceptions.Count + " are exceptions to recurring events.");
            Logboxout("--------------------------------------------------");
            #endregion

            if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogleSimple)
                goto SkipNormalizeItemsInSyncWindow;            

            #region Normalise recurring items in sync window
            Logboxout("Total inc. recurring items spanning sync date range...");
            //Outlook returns recurring items that span the sync date range, Google doesn't
            //So check for master Outlook items occurring before sync date range, and retrieve Google equivalent
            for (int o = outlookEntries.Count - 1; o >= 0; o--) {
                log.Fine("Processing " + o + "/" + (outlookEntries.Count - 1));
                AppointmentItem ai;
                try {
                    if (outlookEntries[o] is AppointmentItem) ai = outlookEntries[o];
                    else if (outlookEntries[o] is MeetingItem) {
                        log.Info("Calendar object appears to be a MeetingItem, so retrieving associated AppointmentItem.");
                        MeetingItem mi = outlookEntries[o] as MeetingItem;
                        outlookEntries[o] = mi.GetAssociatedAppointment(false);
                        ai = outlookEntries[o];
                    } else {
                        log.Warn("Unknown calendar object type - cannot sync it.");
                        log.Debug("Outlook object removed: " + outlookEntries[o].Subject);
                        outlookEntries[o] = (AppointmentItem)OutlookCalendar.ReleaseObject(outlookEntries[o]);
                        outlookEntries.RemoveAt(o);
                        continue;
                    }
                } catch (System.Exception ex) {
                    log.Error("Encountered error casting calendar object to AppointItem - cannot system it.");
                    log.Debug(ex.Message);
                    log.Debug("Outlook object removed: " + outlookEntries[o].Subject);
                    outlookEntries[o] = (AppointmentItem)OutlookCalendar.ReleaseObject(outlookEntries[o]);
                    outlookEntries.RemoveAt(o);
                    continue;
                }

                //Now let's check there's a start/end date - sometimes it can be missing, even though this shouldn't be possible!!
                try {
                    DateTime checkDates = ai.Start;
                    checkDates = ai.End;
                } catch (System.Exception ex) {
                    log.Debug(ex.Message);
                    log.Warn("Calendar item does not have a proper date range - cannot sync it.");
                    outlookEntries.Remove(ai);
                    log.Debug("Outlook object removed: " + ai.Subject);
                    ai = (AppointmentItem)OutlookCalendar.ReleaseObject(ai);
                    continue;
                }

                if (ai.IsRecurring && ai.Start.Date < Settings.Instance.SyncStart && ai.End.Date < Settings.Instance.SyncStart) {
                    //We won't bother getting Google master event if appointment is yearly reoccurring in a month outside of sync range
                    //Otherwise, every sync, the master event will have to be retrieved, compared, concluded nothing's changed (probably) = waste of API calls
                    RecurrencePattern oPattern = ai.GetRecurrencePattern();
                    try {
                        if (oPattern.RecurrenceType.ToString().Contains("Year")) {
                            log.Fine("It's an annual event.");
                            Boolean monthInSyncRange = false;
                            DateTime monthMarker = Settings.Instance.SyncStart;
                            while (monthMarker <= Settings.Instance.SyncEnd && !monthInSyncRange) {
                                if (monthMarker.Month == ai.Start.Month) {
                                    monthInSyncRange = true;
                                }
                                monthMarker = monthMarker.AddMonths(1);
                            }
                            log.Fine("Found it to be " + (monthInSyncRange ? "inside" : "outside") + " sync range.");
                            if (!monthInSyncRange) { outlookEntries.Remove(ai); log.Fine("Removed."); continue; }

                        }
                        Event masterEv = Recurrence.Instance.GetGoogleMasterEvent(ai);
                        if (masterEv != null) {
                            Boolean alreadyCached = false;
                            if (googleEntries.Exists(x => x.Id == masterEv.Id)) {
                                alreadyCached = true;
                            }
                            if (!alreadyCached) googleEntries.Add(masterEv);
                        }
                    } catch (System.Exception ex) {
                        Logboxout("Failed to retrieve master for Google recurring event.");
                        throw ex;
                    } finally {
                        oPattern = (RecurrencePattern)OutlookCalendar.ReleaseObject(oPattern);
                    }
                }
            }
            Logboxout("Outlook " + outlookEntries.Count + ", Google " + googleEntries.Count);
            Logboxout("--------------------------------------------------");
            #endregion

        SkipNormalizeItemsInSyncWindow:
            log.Debug("Skipped Normalizing recurring items in sync window for Outlook -> Google : Simple sync");

            Boolean success = true;
            String bubbleText = "";
            if (Settings.Instance.SyncDirection != SyncDirection.GoogleToOutlook) {
                success = sync_outlookToGoogle(outlookEntries, googleEntries, ref bubbleText);
            }
            if (!success) return false;

            if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogleSimple)
                goto SkipGoogleToOutlookSync;

            if (Settings.Instance.SyncDirection != SyncDirection.OutlookToGoogle) {
                if (bubbleText != "") bubbleText += "\r\n";
                success = sync_googleToOutlook(googleEntries, outlookEntries, ref bubbleText);
            }

        SkipGoogleToOutlookSync:

            if (bubbleText != "") NotificationTray.ShowBubbleInfo(bubbleText);

            for (int o = outlookEntries.Count() - 1; o >= 0; o--) {
                outlookEntries[o] = (AppointmentItem)OutlookCalendar.ReleaseObject(outlookEntries[o]);
                outlookEntries.RemoveAt(o);
            }
            return success;
        }

        private Boolean sync_outlookToGoogle(List<AppointmentItem> outlookEntries, List<Event> googleEntries, ref String bubbleText) {
            log.Debug("Synchronising from Outlook to Google.");
            
            //  Make copies of each list of events (Not strictly needed)
            List<AppointmentItem> googleEntriesToBeCreated = new List<AppointmentItem>(outlookEntries);
            List<Event> googleEntriesToBeDeleted = new List<Event>(googleEntries);
            Dictionary<AppointmentItem, Event> entriesToBeCompared = new Dictionary<AppointmentItem, Event>();

            try {
                GoogleCalendar.Instance.ReclaimOrphanCalendarEntries(ref googleEntriesToBeDeleted, ref outlookEntries);
            } catch (System.Exception ex) {
                MainForm.Instance.Logboxout("Unable to reclaim orphan calendar entries in Google calendar.");
                throw ex;
            }
            try {
                GoogleCalendar.Instance.IdentifyEventDifferences(ref googleEntriesToBeCreated, ref googleEntriesToBeDeleted, entriesToBeCompared);
            } catch (System.Exception ex) {
                MainForm.Instance.Logboxout("Unable to identify differences in Google calendar.");
                throw ex;
            }
            
            Logboxout(googleEntriesToBeDeleted.Count + " Google calendar entries to be deleted.");
            Logboxout(googleEntriesToBeCreated.Count + " Google calendar entries to be created.");

            //Protect against very first syncs which may trample pre-existing non-Outlook events in Google
            if (!Settings.Instance.DisableDelete && !Settings.Instance.ConfirmOnDelete &&
                googleEntriesToBeDeleted.Count == googleEntries.Count && googleEntries.Count > 0) {
                if (MessageBox.Show("All Google events are going to be deleted. Do you want to allow this?" +
                    "\r\nNote, " + googleEntriesToBeCreated.Count + " events will then be created.", "Confirm mass deletion",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.No) 
                {
                    googleEntriesToBeDeleted = new List<Event>();
                }
            }

            int entriesUpdated = 0;
            try {
                #region Delete Google Entries
                if (googleEntriesToBeDeleted.Count > 0) {
                    Logboxout("--------------------------------------------------");
                    Logboxout("Deleting " + googleEntriesToBeDeleted.Count + " Google calendar entries...");
                    try {
                        GoogleCalendar.Instance.DeleteCalendarEntries(googleEntriesToBeDeleted);
                    } catch (UserCancelledSyncException ex) {
                        log.Info(ex.Message);
                        return false;
                    } catch (System.Exception ex) {
                        MainForm.Instance.Logboxout("Unable to delete obsolete entries in Google calendar.");
                        throw ex;
                    }
                    Logboxout("Done.");
                }
                #endregion

                #region Create Google Entries
                if (googleEntriesToBeCreated.Count > 0) {
                    Logboxout("--------------------------------------------------");
                    Logboxout("Creating " + googleEntriesToBeCreated.Count + " Google calendar entries...");
                    try {

                        if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogleSimple) {
                            GoogleCalendar.Instance.CreateCalendarEntries(googleEntriesToBeCreated, ignoreRecurring: true);
                            goto ignoreRecurringOnCreate;
                        }

                        GoogleCalendar.Instance.CreateCalendarEntries(googleEntriesToBeCreated);

                    ignoreRecurringOnCreate:
                        log.Debug("Ignored creating recurring event details for Outlook -> Google : Simple sync");

                    } catch (UserCancelledSyncException ex) {
                        log.Info(ex.Message);
                        return false;
                    } catch (System.Exception ex) {
                        Logboxout("Unable to add new entries into the Google Calendar.");
                        throw ex;
                    }
                    Logboxout("Done.");
                }
                #endregion

                #region Update Google Entries
                if (entriesToBeCompared.Count > 0) {
                    Logboxout("--------------------------------------------------");
                    Logboxout("Comparing " + entriesToBeCompared.Count + " existing Google calendar entries...");
                    try {

                        if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogleSimple) {
                            GoogleCalendar.Instance.UpdateCalendarEntries(entriesToBeCompared, ref entriesUpdated, ignoreRecurring: true);
                            goto ignoreRecurringOnUpdate;
                        }
                        
                        GoogleCalendar.Instance.UpdateCalendarEntries(entriesToBeCompared, ref entriesUpdated);

                    ignoreRecurringOnUpdate:
                        log.Debug("Ignored updating recurring event details for Outlook -> Google : Simple sync");

                    } catch (UserCancelledSyncException ex) {
                        log.Info(ex.Message);
                        return false;
                    } catch (System.Exception ex) {
                        Logboxout("Unable to update existing entries in the Google calendar.");
                        throw ex;
                    }
                    Logboxout(entriesUpdated + " entries updated.");
                }
                #endregion
                Logboxout("--------------------------------------------------");

            } finally {
                bubbleText = "Google: " + googleEntriesToBeCreated.Count + " created; " +
                    googleEntriesToBeDeleted.Count + " deleted; " + entriesUpdated + " updated";

                if (Settings.Instance.SyncDirection == SyncDirection.OutlookToGoogle) {
                    while (entriesToBeCompared.Count() > 0) {
                        OutlookCalendar.ReleaseObject(entriesToBeCompared.Keys.Last());
                        entriesToBeCompared.Remove(entriesToBeCompared.Keys.Last());
                    }
                }
            }
            return true;
        }

        private Boolean sync_googleToOutlook(List<Event> googleEntries, List<AppointmentItem> outlookEntries, ref String bubbleText) {
            log.Debug("Synchronising from Google to Outlook.");
            
            //  Make copies of each list of events (Not strictly needed)
            List<Event> outlookEntriesToBeCreated = new List<Event>(googleEntries);
            List<AppointmentItem> outlookEntriesToBeDeleted = new List<AppointmentItem>(outlookEntries);
            Dictionary<AppointmentItem, Event> entriesToBeCompared = new Dictionary<AppointmentItem, Event>();

            try {
                OutlookCalendar.Instance.ReclaimOrphanCalendarEntries(ref outlookEntriesToBeDeleted, ref googleEntries);
            } catch (System.Exception ex) {
                MainForm.Instance.Logboxout("Unable to reclaim orphan calendar entries in Outlook calendar.");
                throw ex;
            }
            try {
                OutlookCalendar.IdentifyEventDifferences(ref outlookEntriesToBeCreated, ref outlookEntriesToBeDeleted, entriesToBeCompared);
            } catch (System.Exception ex) {
                MainForm.Instance.Logboxout("Unable to identify differences in Outlook calendar.");
                throw ex;
            }
            
            Logboxout(outlookEntriesToBeDeleted.Count + " Outlook calendar entries to be deleted.");
            Logboxout(outlookEntriesToBeCreated.Count + " Outlook calendar entries to be created.");

            //Protect against very first syncs which may trample pre-existing non-Google events in Outlook
            if (!Settings.Instance.DisableDelete && !Settings.Instance.ConfirmOnDelete &&
                outlookEntriesToBeDeleted.Count == outlookEntries.Count && outlookEntries.Count > 0) {
                if (MessageBox.Show("All Outlook events are going to be deleted. Do you want to allow this?" +
                    "\r\nNote, " + outlookEntriesToBeCreated.Count + " events will then be created.", "Confirm mass deletion",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.No) {
                    outlookEntriesToBeDeleted = new List<AppointmentItem>();
                }
            }

            int entriesUpdated = 0;
            try {
                #region Delete Outlook Entries
                if (outlookEntriesToBeDeleted.Count > 0) {
                    Logboxout("--------------------------------------------------");
                    Logboxout("Deleting " + outlookEntriesToBeDeleted.Count + " Outlook calendar entries...");
                    try {
                        OutlookCalendar.Instance.DeleteCalendarEntries(outlookEntriesToBeDeleted);
                    } catch (UserCancelledSyncException ex) {
                        log.Info(ex.Message);
                        return false;
                    } catch (System.Exception ex) {
                        MainForm.Instance.Logboxout("Unable to delete obsolete entries in Google calendar.");
                        throw ex;
                    }
                    Logboxout("Done.");
                }
                #endregion

                #region Create Outlook Entries
                if (outlookEntriesToBeCreated.Count > 0) {
                    Logboxout("--------------------------------------------------");
                    Logboxout("Creating " + outlookEntriesToBeCreated.Count + " Outlook calendar entries...");
                    try {
                        OutlookCalendar.Instance.CreateCalendarEntries(outlookEntriesToBeCreated);
                    } catch (UserCancelledSyncException ex) {
                        log.Info(ex.Message);
                        return false;
                    } catch (System.Exception ex) {
                        Logboxout("Unable to add new entries into the Outlook Calendar.");
                        throw ex;
                    }
                    Logboxout("Done.");
                }
                #endregion

                #region Update Outlook Entries
                if (entriesToBeCompared.Count > 0) {
                    Logboxout("--------------------------------------------------");
                    Logboxout("Comparing " + entriesToBeCompared.Count + " existing Outlook calendar entries...");
                    try {
                        OutlookCalendar.Instance.UpdateCalendarEntries(entriesToBeCompared, ref entriesUpdated);
                    } catch (UserCancelledSyncException ex) {
                        log.Info(ex.Message);
                        return false;
                    } catch (System.Exception ex) {
                        Logboxout("Unable to update new entries into the Outlook calendar.");
                        throw ex;
                    }
                    Logboxout(entriesUpdated + " entries updated.");
                }
                #endregion
                Logboxout("--------------------------------------------------");

            } finally {
                bubbleText += "Outlook: " + outlookEntriesToBeCreated.Count + " created; " +
                    outlookEntriesToBeDeleted.Count + " deleted; " + entriesUpdated + " updated";

                while (entriesToBeCompared.Count() > 0) {
                    OutlookCalendar.ReleaseObject(entriesToBeCompared.Keys.Last());
                    entriesToBeCompared.Remove(entriesToBeCompared.Keys.Last());
                }
            }
            return true;
        }

        #region Compare Event Attributes
        public static Boolean CompareAttribute(String attrDesc, SyncDirection fromTo, String googleAttr, String outlookAttr, System.Text.StringBuilder sb, ref int itemModified) {
            if (googleAttr == null) googleAttr = "";
            if (outlookAttr == null) outlookAttr = "";
            //Truncate long strings
            String googleAttr_stub = (googleAttr.Length > 50) ? googleAttr.Substring(0, 47) + "..." : googleAttr;
            String outlookAttr_stub = (outlookAttr.Length > 50) ? outlookAttr.Substring(0, 47) + "..." : outlookAttr;
            log.Fine("Comparing " + attrDesc);
            if (googleAttr != outlookAttr) {
                if (fromTo == SyncDirection.GoogleToOutlook) {
                    sb.AppendLine(attrDesc + ": " + outlookAttr_stub + " => " + googleAttr_stub);
                } else {
                    sb.AppendLine(attrDesc + ": " + googleAttr_stub + " => " + outlookAttr_stub);
                }
                itemModified++;
                return true;
            }
            return false;
        }
        public static Boolean CompareAttribute(String attrDesc, SyncDirection fromTo, Boolean googleAttr, Boolean outlookAttr, System.Text.StringBuilder sb, ref int itemModified) {
            log.Fine("Comparing " + attrDesc);
            if (googleAttr != outlookAttr) {
                if (fromTo == SyncDirection.GoogleToOutlook) {
                    sb.AppendLine(attrDesc + ": " + outlookAttr + " => " + googleAttr);
                } else {
                    sb.AppendLine(attrDesc + ": " + googleAttr + " => " + outlookAttr);
                }
                itemModified++;
                return true;
            } else {
                return false;
            }
        }
        #endregion

        public void Logboxout(string s, bool newLine = true, bool verbose = false, bool notifyBubble = false) {
            if ((verbose && Settings.Instance.VerboseOutput) || !verbose) {
                String existingText = GetControlPropertyThreadSafe(LogBox, "Text") as String;
                SetControlPropertyThreadSafe(LogBox, "Text", existingText + s + (newLine ? Environment.NewLine : ""));
            }
            if (notifyBubble & Settings.Instance.ShowBubbleTooltipWhenSyncing) {
                NotificationTray.ShowBubbleInfo("Issue encountered.\n" +
                    "Please review output on the main 'Sync' tab", ToolTipIcon.Warning);
            }
            if (verbose) log.Debug(s.TrimEnd());
            else log.Info(s.TrimEnd());
        }

        public enum SyncNotes {
            QuotaExhaustedInfo,
            RecentSubscription,
            SubscriptionPendingExpire,
            SubscriptionExpired,
            NotLogFile
        }
        public void syncNote(SyncNotes syncNote, Object extraData, Boolean show = true) {
            if (!this.tbSyncNote.Visible && !show) return; //Already hidden

            String note = "";
            String url = "";
            String urlStub = "https://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&hosted_button_id=E595EQ7SNDBHA&item_name=";
            String cr = "\r\n";
            switch (syncNote) {
                case SyncNotes.QuotaExhaustedInfo:
                    note =  "  Google's daily free calendar quota is exhausted!" + cr +
                            "     Either wait for new quota at 08:00GMT or     " + cr +
                            "  get yourself guaranteed quota for just £1/month.";
                    url = urlStub + "OGCS Premium for " + Settings.Instance.GaccountEmail;
                    break;
                case SyncNotes.RecentSubscription:
                    note =  "                                                  " + cr +
                            "   Thank you for your subscription and support!   " + cr +
                            "                                                  ";
                    break;
                case SyncNotes.SubscriptionPendingExpire:
                    DateTime expiration = (DateTime)extraData;
                    note =  "  Your annual subscription for guaranteed quota   " + cr +
                            "  for Google calendar usage is expiring on" + expiration.ToString("dd-MMM") + "." + cr +
                            "         Click to renew for just £1/month.        ";
                    url = urlStub + "OGCS Premium renewal from " + expiration.ToString("dd-MMM-yy") + " for " + Settings.Instance.GaccountEmail;
                    break;
                case SyncNotes.SubscriptionExpired:
                    expiration = (DateTime)extraData;
                    note =  "  Your annual subscription for guaranteed quota   " + cr +
                            "    for Google calendar usage expired on " + expiration.ToString("dd-MMM") + "." + cr +
                            "         Click to renew for just £1/month.        ";
                    url = urlStub + "OGCS Premium renewal for " + Settings.Instance.GaccountEmail;
                    break;
                case SyncNotes.NotLogFile:
                    note =  "                       This is not the log file. " + cr +
                            "                                     --------- " + cr +
                            "  Click here to open the folder with OGcalsync.log ";
                    url = "file://" + Program.UserFilePath;
                    break;
            }
            String existingNote = GetControlPropertyThreadSafe(tbSyncNote, "Text") as String;
            if (note != existingNote.Replace("\n", "\r\n") && !show) return; //Trying to hide a note that isn't currently displaying
            SetControlPropertyThreadSafe(tbSyncNote, "Text", note);
            SetControlPropertyThreadSafe(tbSyncNote, "Tag", url);
            SetControlPropertyThreadSafe(tbSyncNote, "Visible", show);
            SetControlPropertyThreadSafe(panelSyncNote, "Visible", show);
        }

        #region Accessors
        public String NextSyncVal {
            get { return lNextSyncVal.Text; }
            set { lNextSyncVal.Text = value; }
        }
        public String LastSyncVal {
            get { return lLastSyncVal.Text; }
            set { lLastSyncVal.Text = value; }
        }
        #endregion

        #region EVENTS
        #region Form actions
        void Save_Click(object sender, EventArgs e) {
            Settings.Instance.Save();
            Settings.Instance.LogSettings();
        }

        public void MainFormShow() {
            this.Show(); //Show minimised back in taskbar
            this.ShowInTaskbar = true;
            this.WindowState = FormWindowState.Normal;
            this.Show(); //Now restore
        }

        private void mainFormResize(object sender, EventArgs e) {
            if (Settings.Instance.MinimiseToTray && this.WindowState == FormWindowState.Minimized) {
                this.ShowInTaskbar = false;
                this.Hide();
                if (Settings.Instance.ShowBubbleWhenMinimising) {
                    NotificationTray.ShowBubbleInfo("OGCS is still running.\r\nClick here to disable this notification.");
                    trayIcon.Tag = "ShowBubbleWhenMinimising";
                } else {
                    trayIcon.Tag = "";
                }
            }
        }

        #region Anti "Log" File
        //Try and stop people pasting the sync summary text as their log file!!!
        private void LogBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyData == (Keys.Control | Keys.C) || e.KeyData == (Keys.Control | Keys.A)) {
                notLogFile();
                e.SuppressKeyPress = false;
            } else {
                e.SuppressKeyPress = true;
            }
        }
        private void LogBox_MouseDown(object sender, MouseEventArgs e) {
            if (e.Button == System.Windows.Forms.MouseButtons.Right) {
                notLogFile();
            }
        }

        private void notLogFile() {
            syncNote(SyncNotes.NotLogFile, null);
            System.Windows.Forms.Application.DoEvents();
            for (int i = 0; i <= 50; i++) {
                System.Threading.Thread.Sleep(100);
                System.Windows.Forms.Application.DoEvents();
            }
            syncNote(SyncNotes.NotLogFile, null, false);
        }
        #endregion

        private void lAboutURL_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start(lAboutURL.Text);
        }

        private void tbSyncNote_Click(object sender, EventArgs e) {
            if (!String.IsNullOrEmpty(tbSyncNote.Tag.ToString())) {
                if (tbSyncNote.Tag.ToString().EndsWith("for ")) {
                    log.Info("User wanted to subscribe, but Google account username is not known :(");
                    DialogResult authorise = MessageBox.Show("Thank you for your interest in subscribing. " +
                       "To kick things off, you'll need to re-authorise OGCS to manage your Google calendar. " +
                       "Would you like to do that now?", "Proceed with authorisation?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (authorise == DialogResult.Yes) {
                        log.Debug("Resetting Google account access.");
                        GoogleCalendar.Instance.Reset();
                        GoogleCalendar.Instance.UserSubscriptionCheck();
                    }
                } else {
                    System.Diagnostics.Process.Start(tbSyncNote.Tag.ToString());
                }
            }
        }
        #endregion
        private void tabAppSettings_DrawItem(object sender, DrawItemEventArgs e) {
            //Want to have horizontal sub-tabs on the left of the Settings tab.
            //Need to handle this manually

            Graphics g = e.Graphics;

            //Tab is rotated, so width is height and vica-versa :-|
            if (tabAppSettings.ItemSize.Width != 35 || tabAppSettings.ItemSize.Height != 75) {
                tabAppSettings.ItemSize = new Size(35, 75);
            }
            // Get the item from the collection.
            TabPage tabPage = tabAppSettings.TabPages[e.Index];

            // Get the real bounds for the tab rectangle.
            Rectangle tabBounds = tabAppSettings.GetTabRect(e.Index);
            Font tabFont = new Font("Microsoft Sans Serif", (float)11, FontStyle.Regular, GraphicsUnit.Pixel);
            Brush textBrush = new SolidBrush(Color.Black);

            if (e.State == DrawItemState.Selected) {
                tabFont = new Font("Microsoft Sans Serif", (float)11, FontStyle.Bold, GraphicsUnit.Pixel);
                Rectangle tabColour = e.Bounds;
                //Blue highlight
                int highlightWidth = 5;
                tabColour.Width = highlightWidth;
                tabColour.X = 0;
                g.FillRectangle(Brushes.Blue, tabColour);
                //Tab main background
                tabColour = e.Bounds;
                tabColour.Width -= highlightWidth;
                tabColour.X += highlightWidth;
                g.FillRectangle(Brushes.White, tabColour);
            } else {
                // Draw a different background color, and don't paint a focus rectangle.
                g.FillRectangle(SystemBrushes.ButtonFace, e.Bounds);
            }

            //Draw white rectangle below the tabs (this would be nice and easy in .Net4)
            Rectangle lastTabRect = tabAppSettings.GetTabRect(tabAppSettings.TabPages.Count - 1);
            tabAppSettings_background.Location = new Point(0, ((lastTabRect.Height + 1) * tabAppSettings.TabPages.Count));
            tabAppSettings_background.Size = new Size(lastTabRect.Width, tabAppSettings.Height - (lastTabRect.Height * tabAppSettings.TabPages.Count));
            e.Graphics.FillRectangle(Brushes.White, tabAppSettings_background);

            // Draw string and align the text.
            StringFormat stringFlags = new StringFormat();
            stringFlags.Alignment = StringAlignment.Far;
            stringFlags.LineAlignment = StringAlignment.Center;
            g.DrawString(tabPage.Text, tabFont, textBrush, tabBounds, new StringFormat(stringFlags));
        }
        #region Outlook settings
        public void rbOutlookDefaultMB_CheckedChanged(object sender, EventArgs e) {
            if (!this.Visible) return;
            if (rbOutlookDefaultMB.Checked) {
                Settings.Instance.OutlookService = OutlookCalendar.Service.DefaultMailbox;
                OutlookCalendar.Instance.Reset();
                gbEWS.Enabled = false;
                //Update available calendars
                cbOutlookCalendars.DataSource = new BindingSource(OutlookCalendar.Instance.CalendarFolders, null);
            }
        }

        private void rbOutlookAltMB_CheckedChanged(object sender, EventArgs e) {
            if (!this.Visible) return;
            if (rbOutlookAltMB.Checked) {
                Settings.Instance.OutlookService = OutlookCalendar.Service.AlternativeMailbox;
                Settings.Instance.MailboxName = ddMailboxName.Text;
                OutlookCalendar.Instance.Reset();
                gbEWS.Enabled = false;
                //Update available calendars
                cbOutlookCalendars.DataSource = new BindingSource(OutlookCalendar.Instance.CalendarFolders, null);
            }
            Settings.Instance.MailboxName = (rbOutlookAltMB.Checked ? ddMailboxName.Text : "");
            ddMailboxName.Enabled = rbOutlookAltMB.Checked;
        }

        private void rbOutlookEWS_CheckedChanged(object sender, EventArgs e) {
            if (!this.Visible) return;
            if (rbOutlookEWS.Checked) {
                Settings.Instance.OutlookService = OutlookCalendar.Service.EWS;
                OutlookCalendar.Instance.Reset();
                gbEWS.Enabled = true;
                //Update available calendars
                cbOutlookCalendars.DataSource = new BindingSource(OutlookCalendar.Instance.CalendarFolders, null);
            }
        }

        private void ddMailboxName_SelectedIndexChanged(object sender, EventArgs e) {
            if (this.Visible) {
                Settings.Instance.MailboxName = ddMailboxName.Text;
                OutlookCalendar.Instance.Reset();
            }
        }
        
        private void txtEWSUser_TextChanged(object sender, EventArgs e) {
            Settings.Instance.EWSuser = txtEWSUser.Text;
        }

        private void txtEWSPass_TextChanged(object sender, EventArgs e) {
            Settings.Instance.EWSpassword = txtEWSPass.Text;
        }

        private void txtEWSServerURL_TextChanged(object sender, EventArgs e) {
            Settings.Instance.EWSserver = txtEWSServerURL.Text;
        }

        public void cbOutlookCalendar_SelectedIndexChanged(object sender, EventArgs e) {
            KeyValuePair<String, MAPIFolder> calendar = (KeyValuePair<String, MAPIFolder>)cbOutlookCalendars.SelectedItem;
            OutlookCalendar.Instance.UseOutlookCalendar = calendar.Value;
        }

        private void cbCategoryFilter_SelectedIndexChanged(object sender, EventArgs e) {
            if (!this.Visible) return;
            Settings.Instance.CategoriesRestrictBy = (cbCategoryFilter.SelectedItem.ToString() == "Include") ?
                Settings.RestrictBy.Include : Settings.RestrictBy.Exclude;
        }

        private void clbCategories_SelectedIndexChanged(object sender, EventArgs e) {
            if (!this.Visible) return;
            
            Settings.Instance.Categories.Clear();
            foreach (object item in clbCategories.CheckedItems) {
                Settings.Instance.Categories.Add(item.ToString());
            }
        }

        #region Datetime Format
        private void cbOutlookDateFormat_SelectedIndexChanged(object sender, EventArgs e) {
            KeyValuePair<string, string> selectedFormat = (KeyValuePair<string, string>)cbOutlookDateFormat.SelectedItem;
            if (selectedFormat.Key != "Custom") {
                tbOutlookDateFormat.Text = selectedFormat.Value;
                if (this.Visible) Settings.Instance.OutlookDateFormat = tbOutlookDateFormat.Text;
            }
            tbOutlookDateFormat.ReadOnly = (selectedFormat.Key != "Custom");
        }

        private void tbOutlookDateFormat_TextChanged(object sender, EventArgs e) {
            try {
                tbOutlookDateFormatResult.Text = DateTime.Now.ToString(tbOutlookDateFormat.Text);
            } catch (System.FormatException) {
                tbOutlookDateFormatResult.Text = "Not a valid date format";
            }
        }

        private void tbOutlookDateFormat_Leave(object sender, EventArgs e) {
            if (String.IsNullOrEmpty(tbOutlookDateFormat.Text) || tbOutlookDateFormatResult.Text == "Not a valid date format") {
                cbOutlookDateFormat.SelectedIndex = 0;
            }
            Settings.Instance.OutlookDateFormat = tbOutlookDateFormat.Text;
        }

        private void btTestOutlookFilter_Click(object sender, EventArgs e) {
            log.Debug("Testing the Outlook filter string.");
            int filterCount = OutlookCalendar.Instance.FilterCalendarEntries(OutlookCalendar.Instance.UseOutlookCalendar.Items, false).Count();
            String msg = "The format '" + tbOutlookDateFormat.Text + "' returns " + filterCount + " calendar items within the date range ";
            msg += Settings.Instance.SyncStart.ToString(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern);
            msg += " and " + Settings.Instance.SyncEnd.ToString(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern);

            log.Info(msg);
            MessageBox.Show(msg, "Date-Time Format Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void urlDateFormats_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start("https://msdn.microsoft.com/en-us/library/az4se3k1%28v=vs.90%29.aspx");
        }
        #endregion
        #endregion
        #region Google settings
        private void GetMyGoogleCalendars_Click(object sender, EventArgs e) {
            this.bGetGoogleCalendars.Text = "Retrieving Calendars...";
            bGetGoogleCalendars.Enabled = false;
            cbGoogleCalendars.Enabled = false;
            List<MyGoogleCalendarListEntry> calendars = null;
            try {
                calendars = GoogleCalendar.Instance.GetCalendars();
            } catch (ApplicationException ex) {
                if (!String.IsNullOrEmpty(ex.Message)) Logboxout(ex.Message);
            } catch (System.Exception ex) {
                MessageBox.Show("Failed to retrieve Google calendars.\r\n" +
                    "Please check the output on the Sync tab for more details.", "Google calendar retrieval failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logboxout("Unable to get the list of Google calendars. The following error occurred:");
                Logboxout(ex.Message);
                if (ex.InnerException != null) Logboxout(ex.InnerException.Message);
                if (Settings.Instance.Proxy.Type == "IE") {
                    if (MessageBox.Show("Please ensure you can access the internet with Internet Explorer.\r\n" +
                        "Test it now? If successful, please retry retrieving your Google calendars.",
                        "Test IE Internet Access",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
                        System.Diagnostics.Process.Start("iexplore.exe", "http://www.google.com");
                    }
                }
            }
            if (calendars != null) {
                cbGoogleCalendars.Items.Clear();
                foreach (MyGoogleCalendarListEntry mcle in calendars) {
                    cbGoogleCalendars.Items.Add(mcle);
                }
                cbGoogleCalendars.SelectedIndex = 0;
            }

            bGetGoogleCalendars.Enabled = true;
            cbGoogleCalendars.Enabled = true;
            this.bGetGoogleCalendars.Text = "Retrieve Calendars";
        }

        private void cbGoogleCalendars_SelectedIndexChanged(object sender, EventArgs e) {
            Settings.Instance.UseGoogleCalendar = (MyGoogleCalendarListEntry)cbGoogleCalendars.SelectedItem;
        }

        private void btResetGCal_Click(object sender, EventArgs e) {
            if (MessageBox.Show("This will reset the Google account you are using to synchronise with.\r\n" +
                "Useful if you want to start syncing to a different account.",
                "Reset Google account?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes) 
            {
                Settings.Instance.UseGoogleCalendar.Id = null;
                Settings.Instance.UseGoogleCalendar.Name = null;
                this.cbGoogleCalendars.Items.Clear();
                GoogleCalendar.Instance.Reset();
            }
        }
        #endregion
        #region Sync options
        #region How
        private void syncDirection_SelectedIndexChanged(object sender, EventArgs e) {
            Settings.Instance.SyncDirection = (SyncDirection)syncDirection.SelectedItem;
            if (Settings.Instance.SyncDirection == SyncDirection.Bidirectional) {
                cbObfuscateDirection.Enabled = true;
                cbObfuscateDirection.SelectedIndex = SyncDirection.OutlookToGoogle.Id - 1;
            } else {
                cbObfuscateDirection.Enabled = false;
                cbObfuscateDirection.SelectedIndex = Settings.Instance.SyncDirection.Id - 1;
            }
            if (Settings.Instance.SyncDirection == SyncDirection.GoogleToOutlook) {
                OutlookCalendar.Instance.DeregisterForPushSync();
                this.cbOutlookPush.Checked = false;
                this.cbOutlookPush.Enabled = false;
                this.cbUseGoogleDefaultReminder.Visible = false;
                this.cbReminderDND.Visible = false;
                this.dtDNDstart.Visible = false;
                this.dtDNDend.Visible = false;
                this.lDNDand.Visible = false;
                cbAddReminders_CheckedChanged(null, null);
            } else {
                this.cbOutlookPush.Enabled = true;
                this.cbUseGoogleDefaultReminder.Visible = true;
                this.cbReminderDND.Visible = true;
                this.dtDNDstart.Visible = true;
                this.dtDNDend.Visible = true;
                this.lDNDand.Visible = true;
                cbAddReminders_CheckedChanged(null, null);
            }
            showWhatPostit();
        }

        private void cbMergeItems_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.MergeItems = cbMergeItems.Checked;
        }

        private void cbConfirmOnDelete_CheckedChanged(object sender, System.EventArgs e) {
            Settings.Instance.ConfirmOnDelete = cbConfirmOnDelete.Checked;
        }

        private void cbDisableDeletion_CheckedChanged(object sender, System.EventArgs e) {
            Settings.Instance.DisableDelete = cbDisableDeletion.Checked;
            cbConfirmOnDelete.Enabled = !cbDisableDeletion.Checked;
        }

        private void cbOfuscate_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.Obfuscation.Enabled = cbOfuscate.Checked;
        }
        
        private void btObfuscateRules_CheckedChanged(object sender, EventArgs e) {
            int minPanelHeight = Convert.ToInt16(109 * magnification);
            int maxPanelHeight = Convert.ToInt16(251 * magnification);
            this.gbSyncOptions_How.BringToFront();
            if ((sender as CheckBox).Checked) {
                while (this.gbSyncOptions_How.Height < maxPanelHeight) {
                    this.gbSyncOptions_How.Height += 2;
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(1);
                }
                this.gbSyncOptions_How.Height = maxPanelHeight;
            } else {
                while (this.gbSyncOptions_How.Height > minPanelHeight && this.Visible) {
                    this.gbSyncOptions_How.Height -= 2;
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(1);
                }
                this.gbSyncOptions_How.Height = minPanelHeight;
            }
        }

        private void cbObfuscateDirection_SelectedIndexChanged(object sender, EventArgs e) {
            Settings.Instance.Obfuscation.Direction = (SyncDirection)cbObfuscateDirection.SelectedItem;
        }

        private void dgObfuscateRegex_Leave(object sender, EventArgs e) {
            Settings.Instance.Obfuscation.SaveRegex(dgObfuscateRegex);
        }
        #endregion
        #region When
        private void tbDaysInThePast_ValueChanged(object sender, EventArgs e) {
            Settings.Instance.DaysInThePast = (int)tbDaysInThePast.Value;
        }

        private void tbDaysInTheFuture_ValueChanged(object sender, EventArgs e) {
            Settings.Instance.DaysInTheFuture = (int)tbDaysInTheFuture.Value;
        }

        private void tbMinuteOffsets_ValueChanged(object sender, EventArgs e) {
            if ((int)tbInterval.Value > 0 && (int)tbInterval.Value < 10 && cbIntervalUnit.SelectedItem.ToString() == "Minutes") {
                if (tbInterval.Value < Convert.ToInt16(tbInterval.Text))
                    tbInterval.Value = 0;
                else
                    tbInterval.Value = 10;
            }
            Settings.Instance.SyncInterval = (int)tbInterval.Value;
            OgcsTimer.SetNextSync();
            NotificationTray.UpdateAutoSyncItems();
        }

        private void cbIntervalUnit_SelectedIndexChanged(object sender, EventArgs e) {
            if (cbIntervalUnit.Text == "Minutes" && (int)tbInterval.Value > 0 && (int)tbInterval.Value < 10) {
                tbInterval.Value = 10;
            }
            Settings.Instance.SyncIntervalUnit = cbIntervalUnit.Text;
            OgcsTimer.SetNextSync();
        }

        private void cbOutlookPush_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.OutlookPush = cbOutlookPush.Checked;
            if (this.Visible) {
                if (cbOutlookPush.Checked) OutlookCalendar.Instance.RegisterForPushSync();
                else OutlookCalendar.Instance.DeregisterForPushSync();
                NotificationTray.UpdateAutoSyncItems();
            }
        }
        #endregion
        #region What
        private void showWhatPostit() {
            Boolean visible = (Settings.Instance.AddDescription &&
                Settings.Instance.SyncDirection == SyncDirection.Bidirectional);
            WhatPostit.Visible = visible;
            cbAddDescription_OnlyToGoogle.Visible = visible;
        }

        private void cbAddDescription_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.AddDescription = cbAddDescription.Checked;
            cbAddDescription_OnlyToGoogle.Enabled = cbAddDescription.Checked;
            showWhatPostit();
        }
        private void cbAddDescription_OnlyToGoogle_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.AddDescription_OnlyToGoogle = cbAddDescription_OnlyToGoogle.Checked;
        }

        private void cbAddReminders_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.AddReminders = cbAddReminders.Checked;
            cbUseGoogleDefaultReminder.Enabled = cbAddReminders.Checked;
            cbReminderDND.Enabled = cbAddReminders.Checked;
            dtDNDstart.Enabled = cbAddReminders.Checked;
            dtDNDend.Enabled = cbAddReminders.Checked;
            lDNDand.Enabled = cbAddReminders.Checked;
        }
        private void cbUseGoogleDefaultReminder_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.UseGoogleDefaultReminder = cbUseGoogleDefaultReminder.Checked;
        }
        private void cbReminderDND_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.ReminderDND = cbReminderDND.Checked;
        }
        private void dtDNDstart_ValueChanged(object sender, EventArgs e) {
            Settings.Instance.ReminderDNDstart = dtDNDstart.Value;
        }
        private void dtDNDend_ValueChanged(object sender, EventArgs e) {
            Settings.Instance.ReminderDNDend = dtDNDend.Value;
        }

        private void cbAddAttendees_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.AddAttendees = cbAddAttendees.Checked;
            this.tbNumberAttendees.Enabled = cbAddAttendees.Checked;
        }
        private void tbNumberAttendees_ValueChanged(object sender, EventArgs e)
        {
            Settings.Instance.NumberAttendees = (int)tbNumberAttendees.Value;
        }
        #endregion
        #endregion
        #region Application settings
        private void cbStartOnStartup_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.StartOnStartup = cbStartOnStartup.Checked;
            Program.ManageStartupRegKey();
        }

        private void cbShowBubbleTooltipsCheckedChanged(object sender, System.EventArgs e) {
            Settings.Instance.ShowBubbleTooltipWhenSyncing = cbShowBubbleTooltips.Checked;
        }

        private void cbStartInTrayCheckedChanged(object sender, System.EventArgs e) {
            Settings.Instance.StartInTray = cbStartInTray.Checked;
        }

        private void cbMinimiseToTrayCheckedChanged(object sender, System.EventArgs e) {
            Settings.Instance.MinimiseToTray = cbMinimiseToTray.Checked;
        }

        private void cbMinimiseNotCloseCheckedChanged(object sender, System.EventArgs e) {
            Settings.Instance.MinimiseNotClose = cbMinimiseNotClose.Checked;
        }

        private void cbPortable_CheckedChanged(object sender, EventArgs e) {
            if (this.Visible) {
                Settings.Instance.Portable = cbPortable.Checked;
                Program.MakePortable(cbPortable.Checked);
            }
        }

        private void cbCreateFiles_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.CreateCSVFiles = cbCreateFiles.Checked;
        }
        
        private void cbLoggingLevel_SelectedIndexChanged(object sender, EventArgs e) {
            Settings.configureLoggingLevel(MainForm.Instance.cbLoggingLevel.Text);
            Settings.Instance.LoggingLevel = MainForm.Instance.cbLoggingLevel.Text.ToUpper();
        }

        private void btLogLocation_Click(object sender, EventArgs e) {
            try {
                log4net.Appender.IAppender[] appenders = log.Logger.Repository.GetAppenders();
                String logFileLocation = (((log4net.Appender.FileAppender)appenders[0]).File);
                logFileLocation = logFileLocation.Substring(0, logFileLocation.LastIndexOf("\\"));
                System.Diagnostics.Process.Start(@logFileLocation);
            } catch {
                System.Diagnostics.Process.Start(@Program.UserFilePath);
            }
        }

        #region Proxy
        private void rbProxyCustom_CheckedChanged(object sender, EventArgs e) {
            bool result = rbProxyCustom.Checked;
            txtProxyServer.Enabled = result;
            txtProxyPort.Enabled = result;
            cbProxyAuthRequired.Enabled = result;
            if (result) {
                result = !string.IsNullOrEmpty(txtProxyUser.Text) && !string.IsNullOrEmpty(txtProxyPassword.Text);
                cbProxyAuthRequired.Checked = result;
                txtProxyUser.Enabled = result;
                txtProxyPassword.Enabled = result;
            }
        }

        private void cbProxyAuthRequired_CheckedChanged(object sender, EventArgs e) {
            bool result = cbProxyAuthRequired.Checked;
            this.txtProxyPassword.Enabled = result;
            this.txtProxyUser.Enabled = result;
        }

        private void gbProxy_Leave(object sender, EventArgs e) {
            applyProxy();
        }
        #endregion
        #endregion

        #region Dev Options
        private void cbGoogleDevOverride_CheckedChanged(object sender, EventArgs e)
        {
            Settings.Instance.OverrideDeveloper = cbGoogleDevOverride.Checked;
            this.txtGoogleDevClientID.Enabled = cbGoogleDevOverride.Checked;
            this.txtGoogleDevClientSecret.Enabled = cbGoogleDevOverride.Checked;
        }
        private void txtGoogleDevClientID_TextChanged(object sender, EventArgs e)
        {
            Settings.Instance.GoogleDeveloperClientID = txtGoogleDevClientID.Text;
        }
        private void txtGoogleDevClientSecret_TextChanged(object sender, EventArgs e)
        {
            Settings.Instance.GoogleDeveloperClientSecret = txtGoogleDevClientSecret.Text;
        }
        private void cbEnableAutoRetry_CheckedChanged(object sender, EventArgs e)
        {
            Settings.Instance.EnableAutoRetry = cbEnableAutoRetry.Checked;
            this.tbAutoRetryDelay.Enabled = cbEnableAutoRetry.Checked;
        }
        private void tbAutoRetryDelay_ValueChanged(object sender, EventArgs e)
        {
            Settings.Instance.AutoRetryDelayMin = (int)tbAutoRetryDelay.Value;
        }
        private void lGoogleConsolLink_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start(lGoogleConsolLink.Text);
        }
        #endregion

        #region Help
        private void linkTShoot_loglevel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            this.tabApp.SelectedTab = this.tabPage_Settings;
            this.tabAppSettings.SelectedTab = this.tabAppSettings.TabPages["tabAppBehaviour"];
        }

        private void linkTShoot_issue_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            System.Diagnostics.Process.Start("https://outlookgooglecalendarsync.codeplex.com/workitem/list/basic");
        }

        private void linkTShoot_logfile_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            this.btLogLocation_Click(null, null);
        }
        #endregion

        private void cbVerboseOutput_CheckedChanged(object sender, EventArgs e) {
            Settings.Instance.VerboseOutput = cbVerboseOutput.Checked;
        }

        private void pbDonate_Click(object sender, EventArgs e) {
            Social.Donate();
        }

        private void btCheckForUpdate_Click(object sender, EventArgs e) {
            Program.checkForUpdate(true);
        }
        private void cbAlphaReleases_CheckedChanged(object sender, EventArgs e) {
            if (this.Visible)
                Settings.Instance.AlphaReleases = cbAlphaReleases.Checked;
        }
        #endregion

        #region Thread safe access to form components
        //private delegate Control getControlThreadSafeDelegate(Control control);
        //Used to update the logbox from the Sync() thread
        public delegate void SetControlPropertyThreadSafeDelegate(Control control, string propertyName, object propertyValue);
        public delegate object GetControlPropertyThreadSafeDelegate(Control control, string propertyName);
        
        //private static Control getControlThreadSafe(Control control) {
        //    if (control.InvokeRequired) {
        //        return (Control)control.Invoke(new getControlThreadSafeDelegate(getControlThreadSafe), new object[] { control });
        //    } else {
        //        return control;
        //    }
        //}
        public object GetControlPropertyThreadSafe(Control control, string propertyName) {
            if (control.InvokeRequired) {
                return control.Invoke(new GetControlPropertyThreadSafeDelegate(GetControlPropertyThreadSafe), new object[] { control, propertyName });
            } else {
                return control.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.GetProperty, null, control, null);
            }
        }
        public void SetControlPropertyThreadSafe(Control control, string propertyName, object propertyValue) {
            if (control.InvokeRequired) {
                control.Invoke(new SetControlPropertyThreadSafeDelegate(SetControlPropertyThreadSafe), new object[] { control, propertyName, propertyValue });
            } else {
                var theObject = control.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.SetProperty, null, control, new object[] { propertyValue });
                if (control.GetType().Name == "TextBox") {
                    TextBox tb = control as TextBox;
                    tb.SelectionStart = tb.Text.Length;
                    tb.ScrollToCaret();
                }
            }
        }
        #endregion

        #region Social Media & Analytics
        private void checkSyncMilestone() {
            Boolean isMilestone = false;
            Int32 syncs = Settings.Instance.CompletedSyncs;
            String blurb = "You've completed " + String.Format("{0:n0}", syncs) + " syncs! Why not let people know how useful this tool is...";

            lMilestone.Text = String.Format("{0:n0}", syncs) + " Syncs!";
            lMilestoneBlurb.Text = blurb;

            switch (syncs) {
                case 10: isMilestone = true; break;
                case 100: isMilestone = true; break;
                case 250: isMilestone = true; break;
                case 1000: isMilestone = true; break;
            }
            if (isMilestone) {

                if (Settings.Instance.EnableAutoRetry)
                    goto SkipSpreadTheWord;

                if (MessageBox.Show(blurb, "Spread the Word", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation) == System.Windows.Forms.DialogResult.OK)
                    tabApp.SelectedTab = tabPage_Social;

                SkipSpreadTheWord:
                log.Debug("Skipped spreading the word");

            }
        }

        private void btSocialTweet_Click(object sender, EventArgs e) {
            Social.Twitter_tweet();
        }
        private void pbSocialTwitterFollow_Click(object sender, EventArgs e) {
            Social.Twitter_follow();
        }

        private void btSocialGplus_Click(object sender, EventArgs e) {
            Social.Google_share();
        }
        private void pbSocialGplusCommunity_Click(object sender, EventArgs e) {
            Social.Google_goToCommunity();
        }

        private void btSocialFB_Click(object sender, EventArgs e) {
            Social.Facebook_share();
        }

        private void btSocialRSSfeed_Click(object sender, EventArgs e) {
            Social.RSS_follow();
        }

        private void btSocialLinkedin_Click(object sender, EventArgs e) {
            Social.Linkedin_share();
        }

        #endregion
    }
}

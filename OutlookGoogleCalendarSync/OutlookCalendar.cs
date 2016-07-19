﻿using Google.Apis.Calendar.v3.Data;
using log4net;
using Microsoft.Office.Interop.Outlook;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OutlookGoogleCalendarSync {
    /// <summary>
    /// Description of OutlookCalendar.
    /// </summary>
    public class OutlookCalendar {
        private static OutlookCalendar instance;
        private static readonly ILog log = LogManager.GetLogger(typeof(OutlookCalendar));
        public OutlookInterface IOutlook;
        
        public static OutlookCalendar Instance {
            get {
                try {
                    if (instance == null || instance.Accounts == null) instance = new OutlookCalendar();
                } catch (System.ApplicationException ex) {
                    throw ex;
                } catch (System.Exception ex) {
                    log.Debug(ex.Message);
                    log.Info("It appears Outlook has been restarted after OGCS was started. Reconnecting...");
                    instance = new OutlookCalendar();
                }
                return instance;
            }
        }
        public PushSyncTimer OgcsPushTimer;
        private String currentUserSMTP {
            get { return IOutlook.CurrentUserSMTP(); }
        }
        public String CurrentUserName {
            get { return IOutlook.CurrentUserName(); }
        }
        public MAPIFolder UseOutlookCalendar {
            get { return IOutlook.UseOutlookCalendar(); }
            set {
                IOutlook.UseOutlookCalendar(value);
                Settings.Instance.UseOutlookCalendar = new MyOutlookCalendarListEntry(value);
            }
        }
        public List<String> Accounts {
            get { return IOutlook.Accounts(); }
        }
        public Dictionary<string, MAPIFolder> CalendarFolders {
            get { return IOutlook.CalendarFolders(); }
        }
        public enum Service {
            DefaultMailbox,
            AlternativeMailbox,
            EWS
        }
        public const String gEventID = "googleEventID";

        public OutlookCalendar() {
            IOutlook = OutlookFactory.getOutlookInterface();
            IOutlook.Connect();
        }

        public void Reset() {
            instance = new OutlookCalendar();
        }

        #region Push Sync
        //Multi-threaded, so need to protect against registering events more than once
        //Simply removing an event handler before adding isn't safe enough
        private int eventHandlerHooks = 0;

        public void RegisterForPushSync() {
            log.Info("Registering for Outlook appointment change events...");
            if (eventHandlerHooks != 0) purgeOutlookEventHandlers();
            
            if (Settings.Instance.SyncDirection != SyncDirection.GoogleToOutlook) {            
                log.Debug("Create the timer for the push synchronisation");
                if (OgcsPushTimer == null) 
                    OgcsPushTimer = new PushSyncTimer();
                if (!OgcsPushTimer.Running())
                    OgcsPushTimer.Switch(true);

                UseOutlookCalendar.Items.ItemAdd += new ItemsEvents_ItemAddEventHandler(appointmentItem_Add);
                UseOutlookCalendar.Items.ItemChange += new ItemsEvents_ItemChangeEventHandler(appointmentItem_Change);
                UseOutlookCalendar.Items.ItemRemove += new ItemsEvents_ItemRemoveEventHandler(appointmentItem_Remove);
                eventHandlerHooks++;
            }
        }

        public void DeregisterForPushSync() {
            log.Info("Deregistering from Outlook appointment change events...");
            purgeOutlookEventHandlers();
            if (OgcsPushTimer != null && OgcsPushTimer.Running())
                OgcsPushTimer.Switch(false);
        }

        private void purgeOutlookEventHandlers() {
            log.Debug("Removing " + eventHandlerHooks + " Outlook event handler hooks.");
            while (eventHandlerHooks > 0) {
                try { UseOutlookCalendar.Items.ItemAdd -= new ItemsEvents_ItemAddEventHandler(appointmentItem_Add); } catch { }
                try { UseOutlookCalendar.Items.ItemChange -= new ItemsEvents_ItemChangeEventHandler(appointmentItem_Change); } catch { }
                try { UseOutlookCalendar.Items.ItemRemove -= new ItemsEvents_ItemRemoveEventHandler(appointmentItem_Remove); } catch { }
                eventHandlerHooks--;
            }
        }

        private void appointmentItem_Add(object Item) {
            if (Settings.Instance.SyncDirection == SyncDirection.GoogleToOutlook) return;

            try {
                log.Debug("Detected Outlook item added.");
                AppointmentItem ai = Item as AppointmentItem;

                DateTime syncMin = DateTime.Today.AddDays(-Settings.Instance.DaysInThePast);
                DateTime syncMax = DateTime.Today.AddDays(+Settings.Instance.DaysInTheFuture + 1);
                if (ai.Start < syncMax && ai.End >= syncMin) {
                    log.Debug(GetEventSummary(ai));
                    log.Debug("Item is in sync range, so push sync flagged for Go.");
                    OgcsPushTimer.ItemsQueued++;
                    log.Info(OgcsPushTimer.ItemsQueued + " items changed since last sync.");
                } else {
                    log.Fine("Item is outside of sync range.");
                }
            } catch (System.Exception ex) {
                log.Error(ex.Message);
            }
        }
        private void appointmentItem_Change(object Item) {
            if (Settings.Instance.SyncDirection == SyncDirection.GoogleToOutlook) return;

            try {
                log.Debug("Detected Outlook item changed.");
                AppointmentItem ai = Item as AppointmentItem;

                DateTime syncMin = DateTime.Today.AddDays(-Settings.Instance.DaysInThePast);
                DateTime syncMax = DateTime.Today.AddDays(+Settings.Instance.DaysInTheFuture + 1);
                if (ai.Start < syncMax && ai.End >= syncMin) {
                    log.Debug(GetEventSummary(ai));
                    log.Debug("Item is in sync range, so push sync flagged for Go.");
                    OgcsPushTimer.ItemsQueued++;
                    log.Info(OgcsPushTimer.ItemsQueued + " items changed since last sync.");
                } else {
                    log.Fine("Item is outside of sync range.");
                }
            } catch (System.Exception ex) {
                log.Error(ex.Message);
            }
        }
        private void appointmentItem_Remove() {
            if (Settings.Instance.SyncDirection == SyncDirection.GoogleToOutlook) return;

            try {
                log.Debug("Detected Outlook item removed, so push sync flagged for Go.");
                OgcsPushTimer.ItemsQueued++;
                log.Info(OgcsPushTimer.ItemsQueued + " items changed since last sync.");
            } catch (System.Exception ex) {
                log.Error(ex.Message);
            }
        }
        #endregion

        public List<AppointmentItem> GetCalendarEntriesInRange(Boolean includeRecurrences = false) {
            List<AppointmentItem> filtered = new List<AppointmentItem>();
            filtered = FilterCalendarEntries(UseOutlookCalendar.Items, includeRecurrences:includeRecurrences);
            
            if (Settings.Instance.CreateCSVFiles) {
                ExportToCSV("Outputting all Appointments to CSV", "outlook_appointments.csv", filtered);
            }
            return filtered;
        }

        public List<AppointmentItem> FilterCalendarEntries(Items OutlookItems, Boolean filterCategories = true, Boolean includeRecurrences = false) {
            //Filtering info @ https://msdn.microsoft.com/en-us/library/cc513841%28v=office.12%29.aspx

            List<AppointmentItem> result = new List<AppointmentItem>();
            if (OutlookItems != null) {
                log.Fine(OutlookItems.Count + " calendar items exist.");

                //OutlookItems.Sort("[Start]", Type.Missing);
                OutlookItems.IncludeRecurrences = false;

                if (includeRecurrences) {
                    OutlookItems.Sort("[Start]", Type.Missing);
                    OutlookItems.IncludeRecurrences = true;
                }

                DateTime min = Settings.Instance.SyncStart;
                DateTime max = Settings.Instance.SyncEnd;

                string filter = "[End] >= '" + min.ToString(Settings.Instance.OutlookDateFormat) + 
                    "' AND [Start] < '" + max.ToString(Settings.Instance.OutlookDateFormat) + "'";
                log.Fine("Filter string: " + filter);
                foreach (AppointmentItem ai in OutlookItems.Restrict(filter)) {
                    try {
                        if (ai.End == min) continue; //Required for midnight to midnight events 
                    } catch (System.Exception ex) {
                        log.Error(ex.Message);
                        log.Error(ex.StackTrace);
                        try {
                            log.Debug("Unable to get End date for: " + OutlookCalendar.GetEventSummary(ai));
                        } catch {
                            log.Error("Appointment item seems unusable!");
                        } 
                        continue;
                    }
                    if (filterCategories) {
                        if (Settings.Instance.CategoriesRestrictBy == Settings.RestrictBy.Include) {
                            if (Settings.Instance.Categories.Count() > 0 && ai.Categories != null && 
                                ai.Categories.Split(new[] { ", " }, StringSplitOptions.None).Intersect(Settings.Instance.Categories).Count() > 0) 
                            {
                                result.Add(ai);
                            }
                        } else if (Settings.Instance.CategoriesRestrictBy == Settings.RestrictBy.Exclude) {
                            if (Settings.Instance.Categories.Count() == 0 || ai.Categories == null ||
                                ai.Categories.Split(new[] { ", " }, StringSplitOptions.None).Intersect(Settings.Instance.Categories).Count() == 0) 
                            {
                                result.Add(ai);
                            }
                        }
                    } else {
                        result.Add(ai);
                    }
                }
            }

            if (includeRecurrences) {
                log.Fine("Found " + result.Count + " Outlook Events over the range.");
                return result;
            }

            log.Fine("Filtered down to "+ result.Count);
            return result;
        }

        #region Create
        public void CreateCalendarEntries(List<Event> events) {
            for (int g = 0; g < events.Count; g++) {
                Event ev = events[g];
                AppointmentItem newAi = IOutlook.UseOutlookCalendar().Items.Add() as AppointmentItem;
                try {
                    newAi = createCalendarEntry(ev);
                } catch (System.Exception ex) {
                    if (!Settings.Instance.VerboseOutput) MainForm.Instance.Logboxout(GoogleCalendar.GetEventSummary(ev));
                    MainForm.Instance.Logboxout("WARNING: Appointment creation failed.\r\n" + ex.Message);
                    if (ex.GetType() != typeof(System.ApplicationException)) log.Error(ex.StackTrace);

                    if (Settings.Instance.EnableAutoRetry) {
                        continue;
                    }

                    if (MessageBox.Show("Outlook appointment creation failed. Continue with synchronisation?", "Sync item failed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        continue;
                    else {
                        newAi = (AppointmentItem)ReleaseObject(newAi);
                        throw new UserCancelledSyncException("User chose not to continue sync.");
                    }
                }

                try {
                    createCalendarEntry_save(newAi, ref ev);
                    events[g] = ev;
                } catch (System.Exception ex) {
                    MainForm.Instance.Logboxout("WARNING: New appointment failed to save.\r\n" + ex.Message);
                    log.Error(ex.StackTrace);

                    if (Settings.Instance.EnableAutoRetry) {
                        continue;
                    }

                    if (MessageBox.Show("New Outlook appointment failed to save. Continue with synchronisation?", "Sync item failed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        continue;
                    else {
                        newAi = (AppointmentItem)ReleaseObject(newAi);
                        throw new UserCancelledSyncException("User chose not to continue sync.");
                    }
                }

                if (ev.Recurrence != null && ev.RecurringEventId == null && Recurrence.Instance.HasExceptions(ev)) {
                    MainForm.Instance.Logboxout("This is a recurring item with some exceptions:-");
                    Recurrence.Instance.CreateOutlookExceptions(newAi, ev);
                    MainForm.Instance.Logboxout("Recurring exceptions completed.");
                }
                newAi = (AppointmentItem)ReleaseObject(newAi);
            }
        }
        
        private AppointmentItem createCalendarEntry(Event ev) {
            string itemSummary = GoogleCalendar.GetEventSummary(ev);
            log.Debug("Processing >> " + itemSummary);
            MainForm.Instance.Logboxout(itemSummary, verbose: true);

            AppointmentItem ai = IOutlook.UseOutlookCalendar().Items.Add() as AppointmentItem;

            //Add the Google event ID into Outlook appointment.
            AddOGCSproperty(ref ai, gEventID, GoogleCalendar.GetOGCSEventID(ev));

            ai.Start = new DateTime();
            ai.End = new DateTime();
            ai.AllDayEvent = (ev.Start.Date != null);
            ai = OutlookCalendar.Instance.IOutlook.WindowsTimeZone_set(ai, ev);
            Recurrence.Instance.BuildOutlookPattern(ev, ai);
            
            ai.Subject = Obfuscate.ApplyRegex(ev.Summary, SyncDirection.GoogleToOutlook);
            if (Settings.Instance.AddDescription && ev.Description != null) ai.Body = ev.Description;
            ai.Location = ev.Location;
            ai.Sensitivity = (ev.Visibility == "private") ? OlSensitivity.olPrivate : OlSensitivity.olNormal;
            ai.BusyStatus = (ev.Transparency == "transparent") ? OlBusyStatus.olFree : OlBusyStatus.olBusy;

            if (Settings.Instance.AddAttendees && ev.Attendees != null) {
                foreach (EventAttendee ea in ev.Attendees) {
                    createRecipient(ea, ai);
                }
            }

            //Reminder alert
            if (Settings.Instance.AddReminders && ev.Reminders != null && ev.Reminders.Overrides != null) {
                foreach (EventReminder reminder in ev.Reminders.Overrides) {
                    if (reminder.Method == "popup") {
                        ai.ReminderSet = true;
                        ai.ReminderMinutesBeforeStart = (int)reminder.Minutes;
                    }
                }
            }
            return ai;
        }

        private static void createCalendarEntry_save(AppointmentItem ai, ref Event ev) {
            if (Settings.Instance.SyncDirection == SyncDirection.Bidirectional) {
                log.Debug("Saving timestamp when OGCS updated appointment.");
                setOGCSlastModified(ref ai);
            }
            
            ai.Save();

            Boolean oKeyExists = false;
            try {
                oKeyExists = ev.ExtendedProperties.Private.ContainsKey(GoogleCalendar.oEntryID);
            } catch {}
            if (Settings.Instance.SyncDirection == SyncDirection.Bidirectional || oKeyExists) {
                log.Debug("Storing the Outlook appointment ID in Google event.");
                GoogleCalendar.AddOutlookID(ref ev, ai);
                GoogleCalendar.Instance.UpdateCalendarEntry_save(ref ev);
            }
        }
        #endregion

        #region Update
        public void UpdateCalendarEntries(Dictionary<AppointmentItem, Event> entriesToBeCompared, ref int entriesUpdated) {
            entriesUpdated = 0;
            foreach (KeyValuePair<AppointmentItem, Event> compare in entriesToBeCompared) {
                int itemModified = 0;
                AppointmentItem ai = IOutlook.UseOutlookCalendar().Items.Add() as AppointmentItem;
                Boolean aiWasRecurring = compare.Key.IsRecurring;
                try {
                    ai = UpdateCalendarEntry(compare.Key, compare.Value, ref itemModified);
                } catch (System.Exception ex) {
                    if (!Settings.Instance.VerboseOutput) MainForm.Instance.Logboxout(GoogleCalendar.GetEventSummary(compare.Value));
                    MainForm.Instance.Logboxout("WARNING: Appointment update failed.\r\n" + ex.Message);
                    log.Error(ex.StackTrace);

                    if (Settings.Instance.EnableAutoRetry) {
                        continue;
                    }

                    if (MessageBox.Show("Outlook appointment update failed. Continue with synchronisation?", "Sync item failed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        continue;
                    else {
                        ai = (AppointmentItem)ReleaseObject(ai);
                        throw new UserCancelledSyncException("User chose not to continue sync.");
                    }
                }

                if (itemModified > 0) {
                    try {
                        updateCalendarEntry_save(ai);
                        entriesUpdated++;
                    } catch (System.Exception ex) {
                        MainForm.Instance.Logboxout("WARNING: Updated appointment failed to save.\r\n" + ex.Message);
                        log.Error(ex.StackTrace);

                        if (Settings.Instance.EnableAutoRetry) {
                            continue;
                        }

                        if (MessageBox.Show("Updated Outlook appointment failed to save. Continue with synchronisation?", "Sync item failed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            continue;
                        else {
                            ai = (AppointmentItem)ReleaseObject(ai); 
                            throw new UserCancelledSyncException("User chose not to continue sync.");
                        }
                    }
                    if (!aiWasRecurring && ai.IsRecurring) {
                        log.Debug("Appointment has changed from single instance to recurring, so exceptions may need processing.");
                        Recurrence.Instance.UpdateOutlookExceptions(ai, compare.Value);
                    }
                } else if (ai != null && ai.RecurrenceState != OlRecurrenceState.olApptMaster) { //Master events are always compared anyway
                    log.Debug("Doing a dummy update in order to update the last modified date.");
                    setOGCSlastModified(ref ai);
                    updateCalendarEntry_save(ai);
                }
                ai = (AppointmentItem)ReleaseObject(ai);
            }
        }

        public AppointmentItem UpdateCalendarEntry(AppointmentItem ai, Event ev, ref int itemModified, Boolean forceCompare = false) {
            if (ai.RecurrenceState == OlRecurrenceState.olApptMaster) { //The exception child objects might have changed
                log.Debug("Processing recurring master appointment.");
            } else {
                if (!(MainForm.Instance.ManualForceCompare || forceCompare)) { //Needed if the exception has just been created, but now needs updating
                    if (Settings.Instance.SyncDirection != SyncDirection.Bidirectional) {
                        if (DateTime.Parse(GoogleCalendar.GoogleTimeFrom(ai.LastModificationTime)) > DateTime.Parse(ev.Updated))
                            return null;
                    } else {
                        if (GoogleCalendar.GetOGCSlastModified(ev).AddSeconds(5) >= DateTime.Parse(ev.Updated))
                            //Google last modified by OGCS
                            return null;
                        if (DateTime.Parse(GoogleCalendar.GoogleTimeFrom(ai.LastModificationTime)) > DateTime.Parse(ev.Updated))
                            return null;
                    }
                }
            }

            String evSummary = GoogleCalendar.GetEventSummary(ev);
            log.Debug("Processing >> " + evSummary);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(evSummary);

            if (ai.RecurrenceState != OlRecurrenceState.olApptMaster) {
                if (ai.AllDayEvent != (ev.Start.DateTime == null)) {
                    sb.AppendLine("All-Day: " + ai.AllDayEvent + " => " + (ev.Start.DateTime == null));
                    ai.AllDayEvent = (ev.Start.DateTime == null);
                    itemModified++;
                }
            }

            RecurrencePattern oPattern = (ai.RecurrenceState == OlRecurrenceState.olApptMaster) ? ai.GetRecurrencePattern() : null;
            Recurrence.Instance.CompareOutlookPattern(ev, ai, SyncDirection.GoogleToOutlook, sb, ref itemModified);

            DateTime evParsedDate = DateTime.Parse(ev.Start.Date ?? ev.Start.DateTime);
            if (MainForm.CompareAttribute("Start time", SyncDirection.GoogleToOutlook,
                GoogleCalendar.GoogleTimeFrom(evParsedDate),
                GoogleCalendar.GoogleTimeFrom(ai.Start), sb, ref itemModified)) 
            {
                if (ai.RecurrenceState == OlRecurrenceState.olApptMaster) {
                    oPattern.PatternStartDate = evParsedDate;
                    oPattern.StartTime = evParsedDate;
                } else {
                    ai.Start = evParsedDate;
                }
            }

            evParsedDate = DateTime.Parse(ev.End.Date ?? ev.End.DateTime);
            if (MainForm.CompareAttribute("End time", SyncDirection.GoogleToOutlook,
                GoogleCalendar.GoogleTimeFrom(evParsedDate),
                GoogleCalendar.GoogleTimeFrom(ai.End), sb, ref itemModified)) 
            {
                if (ai.RecurrenceState == OlRecurrenceState.olApptMaster) {
                    oPattern.EndTime = evParsedDate;
                } else {
                    ai.End = evParsedDate;
                }
            }

            if (oPattern != null) {
                oPattern.Duration = Convert.ToInt32((evParsedDate - DateTime.Parse(ev.Start.Date ?? ev.Start.DateTime)).TotalMinutes);
                oPattern = (RecurrencePattern)ReleaseObject(oPattern);
            }

            if (ai.RecurrenceState == OlRecurrenceState.olApptMaster) {
                if (ev.Recurrence == null || ev.RecurringEventId != null) {
                    log.Debug("Converting to non-recurring events.");
                    ai.ClearRecurrencePattern();
                    itemModified++;
                } else {
                    Recurrence.Instance.UpdateOutlookExceptions(ai, ev);
                }
            } else if (ai.RecurrenceState == OlRecurrenceState.olApptNotRecurring) {
                if (!ai.IsRecurring && ev.Recurrence != null && ev.RecurringEventId == null) {
                    log.Debug("Converting to recurring appointment.");
                    Recurrence.Instance.CreateOutlookExceptions(ai, ev);
                    itemModified++;
                }
            }

            String summaryObfuscated = Obfuscate.ApplyRegex(ev.Summary, SyncDirection.GoogleToOutlook);
            if (MainForm.CompareAttribute("Subject", SyncDirection.GoogleToOutlook, summaryObfuscated, ai.Subject, sb, ref itemModified)) {
                ai.Subject = summaryObfuscated;
            }
            if (!Settings.Instance.AddDescription) ev.Description = "";
            if (Settings.Instance.SyncDirection == SyncDirection.GoogleToOutlook || !Settings.Instance.AddDescription_OnlyToGoogle) {
                if (MainForm.CompareAttribute("Description", SyncDirection.GoogleToOutlook, ev.Description, ai.Body, sb, ref itemModified))
                    ai.Body = ev.Description;
            }

            if (MainForm.CompareAttribute("Location", SyncDirection.GoogleToOutlook, ev.Location, ai.Location, sb, ref itemModified))
                ai.Location = ev.Location;

            String oPrivacy = (ai.Sensitivity == OlSensitivity.olNormal) ? "default" : "private";
            String gPrivacy = ev.Visibility ?? "default";
            if (MainForm.CompareAttribute("Private", SyncDirection.GoogleToOutlook, gPrivacy, oPrivacy, sb, ref itemModified)) {
                ai.Sensitivity = (ev.Visibility != null && ev.Visibility == "private") ? OlSensitivity.olPrivate : OlSensitivity.olNormal;
            }
            String oFreeBusy = (ai.BusyStatus == OlBusyStatus.olFree) ? "transparent" : "opaque";
            String gFreeBusy = ev.Transparency ?? "opaque";
            if (MainForm.CompareAttribute("Free/Busy", SyncDirection.GoogleToOutlook, gFreeBusy, oFreeBusy, sb, ref itemModified)) {
                ai.BusyStatus = (ev.Transparency != null && ev.Transparency == "transparent") ? OlBusyStatus.olFree : OlBusyStatus.olBusy;
            }

            if (Settings.Instance.AddAttendees) {
                if (ev.Description != null && ev.Description.Contains("===--- Attendees ---===")) {
                    //Protect against <v1.2.4 where attendees were stored as text
                    log.Info("This event still has attendee information in the description - cannot sync them.");
                } else if (Settings.Instance.SyncDirection == SyncDirection.Bidirectional &&
                    ev.Attendees != null && ev.Attendees.Count == 0 && ai.Recipients.Count > 150) {
                        log.Info("Attendees not being synced - there are too many ("+ ai.Recipients.Count +") for Google.");
                } else {
                    //Build a list of Outlook attendees. Any remaining at the end of the diff must be deleted.
                    List<Recipient> removeRecipient = new List<Recipient>();
                    if (ai.Recipients != null) {
                        foreach (Recipient recipient in ai.Recipients) {
                            if (recipient.Name != ai.Organizer)
                                removeRecipient.Add(recipient);
                        }
                    }
                    if (ev.Attendees != null) {
                        for (int g = ev.Attendees.Count - 1; g >= 0; g--) {
                            bool foundRecipient = false;
                            EventAttendee attendee = ev.Attendees[g];
                            
                            foreach (Recipient recipient in ai.Recipients) {
                                if (!recipient.Resolved) recipient.Resolve();
                                String recipientSMTP = IOutlook.GetRecipientEmail(recipient);
                                if (recipientSMTP.ToLower() == attendee.Email.ToLower()) {
                                    foundRecipient = true;
                                    removeRecipient.Remove(recipient);

                                    //Optional attendee
                                    bool oOptional = (ai.OptionalAttendees != null && ai.OptionalAttendees.Contains(attendee.DisplayName ?? attendee.Email));
                                    bool gOptional = (attendee.Optional == null) ? false : (bool)attendee.Optional;
                                    if (MainForm.CompareAttribute("Recipient " + recipient.Name + " - Optional Check",
                                        SyncDirection.GoogleToOutlook, gOptional, oOptional, sb, ref itemModified)) {
                                        if (gOptional) {
                                            recipient.Type = (int)OlMeetingRecipientType.olOptional;
                                        } else {
                                            recipient.Type = (int)OlMeetingRecipientType.olRequired;
                                        }
                                    }
                                    //Response is readonly in Outlook :(
                                    break;
                                }
                            }
                            if (!foundRecipient &&
                                (attendee.DisplayName != ai.Organizer)) //Attendee in Google is owner in Outlook, so can't also be added as a recipient)
                                {
                                sb.AppendLine("Recipient added: " + (attendee.DisplayName ?? attendee.Email));
                                createRecipient(attendee, ai);
                                itemModified++;
                            }
                        }
                    }

                    foreach (Recipient recipient in removeRecipient) {
                        sb.AppendLine("Recipient removed: " + recipient.Name);
                        recipient.Delete();
                        itemModified++;
                    }
                }
            }
            //Reminders
            if (Settings.Instance.AddReminders) {
                if (ev.Reminders.Overrides != null) {
                    //Find the popup reminder in Google
                    for (int r = ev.Reminders.Overrides.Count - 1; r >= 0; r--) {
                        EventReminder reminder = ev.Reminders.Overrides[r];
                        if (reminder.Method == "popup") {
                            if (ai.ReminderSet) {
                                if (MainForm.CompareAttribute("Reminder", SyncDirection.GoogleToOutlook, reminder.Minutes.ToString(), ai.ReminderMinutesBeforeStart.ToString(), sb, ref itemModified)) {
                                    ai.ReminderMinutesBeforeStart = (int)reminder.Minutes;
                                }
                            } else {
                                sb.AppendLine("Reminder: nothing => " + reminder.Minutes);
                                ai.ReminderSet = true;
                                ai.ReminderMinutesBeforeStart = (int)reminder.Minutes;
                                itemModified++;
                            } //if Outlook reminders set
                        } //if google reminder found
                    } //foreach reminder

                } else { //no google reminders set
                    if (ai.ReminderSet && IsOKtoSyncReminder(ai)) {
                        sb.AppendLine("Reminder: " + ai.ReminderMinutesBeforeStart + " => removed");
                        ai.ReminderSet = false;
                        itemModified++;
                    }
                }
            }
            if (itemModified > 0) {
                MainForm.Instance.Logboxout(sb.ToString(), false, verbose: true);
                MainForm.Instance.Logboxout(itemModified + " attributes updated.", verbose: true);
                System.Windows.Forms.Application.DoEvents();
            }
            return ai;
        }

        private void updateCalendarEntry_save(AppointmentItem ai) {
            if (Settings.Instance.SyncDirection == SyncDirection.Bidirectional) {
                log.Debug("Saving timestamp when OGCS updated appointment.");
                setOGCSlastModified(ref ai);
            }
            ai.Save();
        }
        #endregion

        #region Delete
        public void DeleteCalendarEntries(List<AppointmentItem> oAppointments) {
            for (int o = oAppointments.Count - 1; o >= 0; o--) {
                AppointmentItem ai = oAppointments[o];
                Boolean doDelete = false;
                try {
                    doDelete = deleteCalendarEntry(ai);
                } catch (System.Exception ex) {
                    if (!Settings.Instance.VerboseOutput) MainForm.Instance.Logboxout(OutlookCalendar.GetEventSummary(ai));
                    MainForm.Instance.Logboxout("WARNING: Appointment deletion failed.\r\n" + ex.Message);
                    log.Error(ex.StackTrace);
                    if (MessageBox.Show("Outlook appointment deletion failed. Continue with synchronisation?", "Sync item failed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        continue;
                    else {
                        ai = (AppointmentItem)ReleaseObject(ai);
                        throw new UserCancelledSyncException("User chose not to continue sync.");
                    }
                }

                try {
                    if (doDelete) deleteCalendarEntry_save(ai);
                    else oAppointments.Remove(ai);
                } catch (System.Exception ex) {
                    MainForm.Instance.Logboxout("WARNING: Deleted appointment failed to remove.\r\n" + ex.Message);
                    log.Error(ex.StackTrace);

                    if (Settings.Instance.EnableAutoRetry) {
                        continue;
                    }

                    if (MessageBox.Show("Deleted Outlook appointment failed to remove. Continue with synchronisation?", "Sync item failed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        continue;
                    else {
                        throw new UserCancelledSyncException("User chose not to continue sync.");
                    }
                } finally {
                    ai = (AppointmentItem)ReleaseObject(ai);
                }
            }
        }
        
        private Boolean deleteCalendarEntry(AppointmentItem ai) {
            String eventSummary = GetEventSummary(ai);
            Boolean doDelete = true;

            if (Settings.Instance.ConfirmOnDelete) {
                if (MessageBox.Show("Delete " + eventSummary + "?", "Deletion Confirmation",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No) {
                    doDelete = false;
                    MainForm.Instance.Logboxout("Not deleted: " + eventSummary);
                } else {
                    MainForm.Instance.Logboxout("Deleted: " + eventSummary);
                }
            } else {
                MainForm.Instance.Logboxout(eventSummary, verbose: true);
            }
            return doDelete;
        }

        private void deleteCalendarEntry_save(AppointmentItem ai) {
            ai.Delete();
        }
        #endregion
        
        public void ReclaimOrphanCalendarEntries(ref List<AppointmentItem> oAppointments, ref List<Event> gEvents) {
            log.Debug("Looking for orphaned items to reclaim...");

            //This is needed for people migrating from other tools, which do not have our GoogleID extendedProperty
            List<AppointmentItem> unclaimedAi = new List<AppointmentItem>();

            for (int o = oAppointments.Count-1; o>=0; o--){
                AppointmentItem ai = oAppointments[o];
                //Find entries with no Google ID
                if (ai.UserProperties[gEventID] == null) {
                    unclaimedAi.Add(ai);
                    
                    //Use simple matching on start,end,subject,location to pair events
                    String sigAi = signature(ai);
                    foreach (Event ev in gEvents) {
                        String sigEv = GoogleCalendar.signature(ev);
                        if (String.IsNullOrEmpty(sigEv)) {
                            gEvents.Remove(ev);
                            break;
                        }

                        if (Settings.Instance.Obfuscation.Enabled) {
                            if (Settings.Instance.Obfuscation.Direction == SyncDirection.OutlookToGoogle)
                                sigAi = Obfuscate.ApplyRegex(sigAi, SyncDirection.OutlookToGoogle);
                            else
                                sigEv = Obfuscate.ApplyRegex(sigEv, SyncDirection.GoogleToOutlook);
                        }
                        if (sigAi == sigEv) {
                            AddOGCSproperty(ref ai, gEventID, GoogleCalendar.GetOGCSEventID(ev));
                            updateCalendarEntry_save(ai);
                            unclaimedAi.Remove(ai);
                            MainForm.Instance.Logboxout("Reclaimed: " + GetEventSummary(ai), verbose: true);
                            break;
                        }
                    }
                }
            }
            if ((Settings.Instance.SyncDirection == SyncDirection.GoogleToOutlook ||
                    Settings.Instance.SyncDirection == SyncDirection.Bidirectional) &&
                unclaimedAi.Count > 0 &&
                !Settings.Instance.MergeItems && !Settings.Instance.DisableDelete && !Settings.Instance.ConfirmOnDelete) {
                    
                if (MessageBox.Show(unclaimedAi.Count + " Outlook calendar items can't be matched to Google.\r\n" +
                    "Remember, it's recommended to have a dedicated Outlook calendar to sync with, " +
                    "or you may wish to merge with unmatched events. Continue with deletions?",
                    "Delete unmatched Outlook items?", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.No) {

                    foreach (AppointmentItem ai in unclaimedAi) {
                        oAppointments.Remove(ai);
                    }
                }
            }
        }

        private void createRecipient(EventAttendee ea, AppointmentItem ai) {
            if (IOutlook.CurrentUserSMTP().ToLower() != ea.Email) {
                Recipient recipient = ai.Recipients.Add(ea.DisplayName + "<" + ea.Email + ">");
                recipient.Resolve();
                //ReadOnly: recipient.Type = (int)((bool)ea.Organizer ? OlMeetingRecipientType.olOrganizer : OlMeetingRecipientType.olRequired);
                recipient.Type = (int)(ea.Optional == null ? OlMeetingRecipientType.olRequired : ((bool)ea.Optional ? OlMeetingRecipientType.olOptional : OlMeetingRecipientType.olRequired));
                //ReadOnly: ea.ResponseStatus
            }
        }

        #region STATIC functions
        public static Microsoft.Office.Interop.Outlook.Application AttachToOutlook() {
            Microsoft.Office.Interop.Outlook.Application oApp;
            if (System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Count() > 0) {
                log.Debug("Attaching to the already running Outlook process.");
                try {
                    oApp = System.Runtime.InteropServices.Marshal.GetActiveObject("Outlook.Application") as Microsoft.Office.Interop.Outlook.Application;
                } catch (SystemException ex) {
                    log.Debug("Attachment failed. Is Outlook running fully, or perhaps just the 'reminders' window?");
                    log.Debug(ex.Message);
                    oApp = openOutlook();
                }
            } else {
                oApp = openOutlook();
            }
            return oApp;
        }
        private static Microsoft.Office.Interop.Outlook.Application openOutlook() {
            Microsoft.Office.Interop.Outlook.Application oApp;
            log.Debug("Starting a new instance of Outlook.");
            try {
                oApp = new Microsoft.Office.Interop.Outlook.Application();
            } catch (System.Runtime.InteropServices.COMException ex) {
                oApp = null;
                if (ex.ErrorCode == -2147221164) {
                    log.Error(ex.Message);
                    System.Diagnostics.Process.Start("https://outlookgooglecalendarsync.codeplex.com/workitem/list/basic");
                    throw new ApplicationException("Outlook does not appear to be installed!\nThis is a pre-requisite for this software.");
                } else {
                    log.Error("COM Exception encountered.");
                    log.Error(ex.ToString());
                    System.Diagnostics.Process.Start(@Program.UserFilePath);
                    System.Diagnostics.Process.Start("https://outlookgooglecalendarsync.codeplex.com/workitem/list/basic");
                    throw new ApplicationException("COM exception encountered. Please log an Issue on CodePlex and upload your OGcalsync.log file.");
                }
            } catch (System.Exception ex) {
                log.Warn("Early binding to Outlook appears to have failed.");
                log.Debug(ex.Message);
                log.Debug(ex.StackTrace);
                log.Debug(ex.GetType().ToString());
                log.Debug("Could try late binding??");
                //System.Type oAppType = System.Type.GetTypeFromProgID("Outlook.Application");
                //ApplicationClass oAppClass = System.Activator.CreateInstance(oAppType) as ApplicationClass;
                //oApp = oAppClass.CreateObject("Outlook.Application") as Microsoft.Office.Interop.Outlook.Application;
                throw ex;
            }
            return oApp;
        }

        public static string signature(AppointmentItem ai) {
            return (ai.Subject + ";" + GoogleCalendar.GoogleTimeFrom(ai.Start) + ";" + GoogleCalendar.GoogleTimeFrom(ai.End)).Trim();
        }

        public static void ExportToCSV(String action, String filename, List<AppointmentItem> ais) {
            log.Debug(action);

            TextWriter tw;
            try {
                tw = new StreamWriter(Path.Combine(Program.UserFilePath, filename));
            } catch (System.Exception ex) {
                MainForm.Instance.Logboxout("Failed to create CSV file '" + filename + "'.");
                log.Error("Error opening file '" + filename + "' for writing.");
                log.Error(ex.Message);
                return;
            }
            try {
                String CSVheader = "Start Time,Finish Time,Subject,Location,Description,Privacy,FreeBusy,";
                CSVheader += "Required Attendees,Optional Attendees,Reminder Set,Reminder Minutes,Outlook ID,Google ID";
                tw.WriteLine(CSVheader);
                foreach (AppointmentItem ai in ais) {
                    try {
                        tw.WriteLine(exportToCSV(ai));
                    } catch (System.Exception ex) {
                        MainForm.Instance.Logboxout("Failed to output following Outlook appointment to CSV:-");
                        MainForm.Instance.Logboxout(GetEventSummary(ai));
                        log.Error(ex.Message);
                    }
                }
            } catch {
                MainForm.Instance.Logboxout("Failed to output Outlook events to CSV.");
            } finally {
                if (tw != null) tw.Close();
            }
            log.Debug("Done.");
        }
        private static string exportToCSV(AppointmentItem ai) {
            System.Text.StringBuilder csv = new System.Text.StringBuilder();
            
            csv.Append(GoogleCalendar.GoogleTimeFrom(ai.Start) + ",");
            csv.Append(GoogleCalendar.GoogleTimeFrom(ai.End) + ",");
            csv.Append("\"" + ai.Subject + "\",");
            
            if (ai.Location == null) csv.Append(",");
            else csv.Append("\"" + ai.Location + "\",");

            if (ai.Body == null) csv.Append(",");
            else {
                String csvBody = ai.Body.Replace("\"", "");
                csvBody = csvBody.Replace("\r\n", " ");
                csv.Append("\"" + csvBody.Substring(0, System.Math.Min(csvBody.Length, 100)) + "\",");
            }
            
            csv.Append("\"" + ai.Sensitivity.ToString() + "\",");
            csv.Append("\"" + ai.BusyStatus.ToString() + "\",");
            csv.Append("\"" + (ai.RequiredAttendees==null?"":ai.RequiredAttendees) + "\",");
            csv.Append("\"" + (ai.OptionalAttendees==null?"":ai.OptionalAttendees) + "\",");
            csv.Append(ai.ReminderSet + ",");
            csv.Append(ai.ReminderMinutesBeforeStart.ToString() + ",");
            csv.Append(OutlookCalendar.GetOGCSGlobalApptID(ai) + ",");
            if (ai.UserProperties[gEventID] != null)
                csv.Append(ai.UserProperties[gEventID].Value.ToString());

            return csv.ToString();
        }

        public static string GetEventSummary(AppointmentItem ai) {
            String eventSummary = "";
            try {
                if (ai.AllDayEvent) {
                    log.Fine("GetSummary - all day event");
                    eventSummary += ai.Start.Date.ToShortDateString();
                } else {
                    log.Fine("GetSummary - not all day event");
                    eventSummary += ai.Start.ToShortDateString() + " " + ai.Start.ToShortTimeString();
                }
                eventSummary += " " + (ai.IsRecurring ? "(R) " : "") + "=> ";
                eventSummary += '"' + ai.Subject + '"';

            } catch (System.Exception ex) {
                log.Warn("Failed to get appointment summary: " + eventSummary);
                log.Error(ex.Message);
                log.Error(ex.StackTrace);
            } 
            return eventSummary;
        }

        public static void IdentifyEventDifferences(
            ref List<Event> google,             //need creating
            ref List<AppointmentItem> outlook,  //need deleting
            Dictionary<AppointmentItem, Event> compare) {
            log.Debug("Comparing Google events to Outlook items...");

            // Count backwards so that we can remove found items without affecting the order of remaining items
            String oEventID;
            for (int o = outlook.Count - 1; o >= 0; o--) {
                if (getOGCSproperty(outlook[o], gEventID, out oEventID)) {
                    for (int g = google.Count - 1; g >= 0; g--) {
                        if (oEventID == google[g].Id.ToString()) {
                            compare.Add(outlook[o], google[g]);
                            outlook.Remove(outlook[o]);
                            google.Remove(google[g]);
                            break;
                        }
                    }
                } else if (Settings.Instance.MergeItems) {
                    //Remove the non-Google item so it doesn't get deleted
                    outlook.Remove(outlook[o]);
                }
            }

            if (Settings.Instance.DisableDelete) {
                outlook = new List<AppointmentItem>();
            }
            if (Settings.Instance.SyncDirection == SyncDirection.Bidirectional) {
                //Don't recreate any items that have been deleted in Outlook
                for (int g = google.Count - 1; g >= 0; g--) {
                    if (GoogleCalendar.GetOGCSproperty(google[g], GoogleCalendar.oEntryID))
                        google.Remove(google[g]);
                }
                //Don't delete any items that aren't yet in Google or just created in Google during this sync
                for (int o = outlook.Count - 1; o >= 0; o--) {
                    if (!getOGCSproperty(outlook[o], gEventID) ||
                        outlook[o].LastModificationTime > Settings.Instance.LastSyncDate)
                        outlook.Remove(outlook[o]);
                }
            }
            if (Settings.Instance.CreateCSVFiles) {
                ExportToCSV("Appointments for deletion in Outlook", "outlook_delete.csv", outlook);
                GoogleCalendar.ExportToCSV("Events for creation in Outlook", "outlook_create.csv", google);
            }
        }

        public static object ReleaseObject(object obj) {
            try {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(obj);
            } catch { }
            return null;
        }

        public Boolean IsOKtoSyncReminder(AppointmentItem ai) {
            if (Settings.Instance.ReminderDND) {
                DateTime alarm;
                if (ai.ReminderSet)
                    alarm = ai.Start.Date.AddMinutes(-ai.ReminderMinutesBeforeStart);
                else {
                    if (Settings.Instance.UseGoogleDefaultReminder && GoogleCalendar.Instance.MinDefaultReminder != long.MinValue) {
                        log.Fine("Using default Google reminder value: " + GoogleCalendar.Instance.MinDefaultReminder);
                        alarm = ai.Start.Date.AddMinutes(-GoogleCalendar.Instance.MinDefaultReminder);
                    } else
                        return false;
                }
                return isOKtoSyncReminder(alarm);
            }
            return true;
        }
        private Boolean isOKtoSyncReminder(DateTime alarm) {
            if (Settings.Instance.ReminderDNDstart.TimeOfDay > Settings.Instance.ReminderDNDend.TimeOfDay) {
                //eg 22:00 to 06:00
                //Make sure end time is the day following the start time
                Settings.Instance.ReminderDNDstart = alarm.Date.Add(Settings.Instance.ReminderDNDstart.TimeOfDay);
                Settings.Instance.ReminderDNDend = alarm.Date.AddDays(1).Add(Settings.Instance.ReminderDNDend.TimeOfDay);

                if (alarm > Settings.Instance.ReminderDNDstart && alarm < Settings.Instance.ReminderDNDend) {
                    log.Debug("Reminder (@" + alarm.ToString("HH:mm") + ") falls in DND range - not synced.");
                    return false;
                } else
                    return true;

            } else {
                //eg 01:00 to 06:00
                if (alarm.TimeOfDay < Settings.Instance.ReminderDNDstart.TimeOfDay ||
                    alarm.TimeOfDay > Settings.Instance.ReminderDNDend.TimeOfDay) {
                    return true;
                } else {
                    log.Debug("Reminder (@" + alarm.ToString("HH:mm") + ") falls in DND range - not synced.");
                    return false;
                }
            }
        }

        #region OGCS Outlook properties
        public static void AddOGCSproperty(ref AppointmentItem ai, String key, String value) {
            if (!getOGCSproperty(ai, key)) 
                ai.UserProperties.Add(key, OlUserPropertyType.olText);
            ai.UserProperties[key].Value = value;
        }
        private static void addOGCSproperty(ref AppointmentItem ai, String key, DateTime value) {
            if (!getOGCSproperty(ai, key)) 
                ai.UserProperties.Add(key, OlUserPropertyType.olDateTime);
            ai.UserProperties[key].Value = value;
        }

        private static Boolean getOGCSproperty(AppointmentItem ai, String key) {
            String throwAway;
            return getOGCSproperty(ai, key, out throwAway);
        }
        private static Boolean getOGCSproperty(AppointmentItem ai, String key, out String value) {
            UserProperty prop = ai.UserProperties.Find(key);
            if (prop == null) {
                value = null;
                return false;
            } else {
                value = prop.Value.ToString();
                return true;
            }
        }
        private static Boolean getOGCSproperty(AppointmentItem ai, String key, out DateTime value) {
            UserProperty prop = ai.UserProperties.Find(key);
            if (prop == null) {
                value = new DateTime();
                return false;
            } else {
                value = (DateTime)prop.Value;
                return true;
            }
        }

        public static DateTime GetOGCSlastModified(AppointmentItem ai) {
            DateTime lastModded;
            getOGCSproperty(ai, Program.OGCSmodified, out lastModded);
            return lastModded;
        }
        private static void setOGCSlastModified(ref AppointmentItem ai) {
            addOGCSproperty(ref ai, Program.OGCSmodified, DateTime.Now);
        }

        public static String GetOGCSGlobalApptID(AppointmentItem ai) {
            if (Settings.Instance.SyncDirection != SyncDirection.OutlookToGoogleSimple)
                return OutlookCalendar.Instance.IOutlook.GetGlobalApptID(ai);
            else
                return GetEventSummary(ai) + OutlookCalendar.Instance.IOutlook.GetGlobalApptID(ai);
        }
        #endregion
        #endregion

    }
}

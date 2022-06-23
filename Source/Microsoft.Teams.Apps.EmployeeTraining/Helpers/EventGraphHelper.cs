// <copyright file="EventGraphHelper.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Microsoft.Teams.Apps.EmployeeTraining.Helpers
{
    extern alias BetaLib;

    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;
    using System.Web;
    using Microsoft.ApplicationInsights;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Exchange.WebServices.Data;
    using Microsoft.Extensions.Localization;
    using Microsoft.Extensions.Options;
    using Microsoft.Graph;
    using Microsoft.Teams.Apps.EmployeeTraining.Models;
    using Microsoft.Teams.Apps.EmployeeTraining.Models.Configuration;
#pragma warning disable SA1135 // Referring BETA package of MS Graph SDK.
    using Beta = BetaLib.Microsoft.Graph;
#pragma warning restore SA1135 // Referring BETA package of MS Graph SDK.
    using EventType = Microsoft.Teams.Apps.EmployeeTraining.Models.EventType;

    /// <summary>
    /// Implements the methods that are defined in <see cref="IEventGraphHelper"/>.
    /// </summary>
    public class EventGraphHelper : IEventGraphHelper
    {
        /// <summary>
        /// Instance service email;
        /// </summary>
        private readonly string serviceEmail;

        /// <summary>
        /// Instance service password;
        /// </summary>
        private readonly string servicePass;

        /// <summary>
        /// Represents a set of key/value application configuration properties for Azure.
        /// </summary>
        private readonly IOptions<AzureVaultSettings> azureVaultOptions;

        /// <summary>
        /// Instance of graph service client for delegated requests.
        /// </summary>
        private readonly GraphServiceClient delegatedGraphClient;

        /// <summary>
        /// Instance of graph service client for application level requests.
        /// </summary>
        private readonly GraphServiceClient applicationGraphClient;

        /// <summary>
        /// Instance of BETA graph service client for application level requests.
        /// </summary>
        private readonly Beta.GraphServiceClient applicationBetaGraphClient;

        /// <summary>
        /// The current culture's string localizer
        /// </summary>
        private readonly IStringLocalizer<Strings> localizer;

        /// <summary>
        /// Graph helper for operations related user.
        /// </summary>
        private readonly IUserGraphHelper userGraphHelper;

        /// <summary>
        /// Instance onPremises user;
        /// </summary>
        private bool isOnPremUser;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventGraphHelper"/> class.
        /// </summary>
        /// <param name="tokenAcquisitionHelper">Helper to get user access token for specified Graph scopes.</param>
        /// <param name="httpContextAccessor">HTTP context accessor for getting user claims.</param>
        /// <param name="localizer">The current culture's string localizer.</param>
        /// <param name="userGraphHelper">Graph helper for operations related user.</param>
        /// <param name="azureVaultOptions">A set of key/value application configuration properties for Key Vault.</param>
        public EventGraphHelper(
            ITokenAcquisitionHelper tokenAcquisitionHelper,
            IHttpContextAccessor httpContextAccessor,
            IStringLocalizer<Strings> localizer,
            IUserGraphHelper userGraphHelper,
            IOptions<AzureVaultSettings> azureVaultOptions)
        {
            this.localizer = localizer;
            this.userGraphHelper = userGraphHelper;
            httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            this.azureVaultOptions = azureVaultOptions ?? throw new ArgumentNullException(nameof(azureVaultOptions));
            this.serviceEmail = this.azureVaultOptions.Value.ServiceEmail;
            this.servicePass = this.azureVaultOptions.Value.ServicePassword;

            var oidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";
            var userObjectId = httpContextAccessor.HttpContext.User.Claims?
                .FirstOrDefault(claim => oidClaimType.Equals(claim.Type, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrEmpty(userObjectId))
            {
                var jwtToken = AuthenticationHeaderValue.Parse(httpContextAccessor.HttpContext.Request.Headers["Authorization"].ToString()).Parameter;

                this.delegatedGraphClient = GraphServiceClientFactory.GetAuthenticatedGraphClient(async () =>
                {
                    return await tokenAcquisitionHelper.GetUserAccessTokenAsync(userObjectId, jwtToken);
                });

                this.applicationBetaGraphClient = GraphServiceClientFactory.GetAuthenticatedBetaGraphClient(async () =>
                {
                    return await tokenAcquisitionHelper.GetApplicationAccessTokenAsync();
                });

                this.applicationGraphClient = GraphServiceClientFactory.GetAuthenticatedGraphClient(async () =>
                {
                    return await tokenAcquisitionHelper.GetApplicationAccessTokenAsync();
                });

                this.isOnPremUser = this.delegatedGraphClient.Me.Request().Select("onPremisesSyncEnabled").GetAsync().Result.OnPremisesSyncEnabled.HasValue;
            }
        }

        /// <summary>
        /// Instance create appointemnt or delete appointment;
        /// </summary>
        private enum CreateUpdate
        {
            CreateAppointment,
            UpdateAppointment,
        }

        /// <summary>
        /// Gets or sets appointemnt;
        /// </summary>
        private Appointment EventAppointment { get; set; }

        /// <summary>
        /// Cancel calendar event.
        /// </summary>
        /// <param name="eventGraphId">Event Id received from Graph.</param>
        /// <param name="createdByUserId">User Id who created event.</param>
        /// <param name="comment">Cancellation comment.</param>
        /// <param name="telemetryClient">telemetry</param>
        /// <returns>True if event cancellation is successful.</returns>
        public async Task<bool> CancelEventAsync(string eventGraphId, string createdByUserId, string comment, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("CancelEventAsync CALLED");

                Item item;
                ExchangeService service;

                ItemId eventId = eventGraphId;
                var user = await this.delegatedGraphClient.Users[createdByUserId].Request().GetAsync();
                string userPrincipal = user.UserPrincipalName;

                bool isCreatorOnPrem = this.delegatedGraphClient.Users[createdByUserId].Request().Select("onPremisesSyncEnabled").GetAsync().Result.OnPremisesSyncEnabled.HasValue;

                if (isCreatorOnPrem)
                {
                    telemetryClient.TrackEvent("Creator is OnPrem");
                    try
                    {
                        service = this.Service(userPrincipal, telemetryClient);
                        telemetryClient.TrackEvent("Service creation SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Service creation FAIL. {ex.Message}"));
                        return false;
                    }

                    try
                    {
                        item = Item.Bind(service, eventId);
                        telemetryClient.TrackEvent("Item binding SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Item binding FAIL. {ex.Message}"));
                        return false;
                    }

                    try
                    {
                        item.Delete(DeleteMode.MoveToDeletedItems);
                        telemetryClient.TrackTrace($"{item.Subject} cancelled SUCCESS");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"{item.Subject} cancelled FAIL. {ex.Message}"));
                        return false;
                    }
                }
                else
                {
                    telemetryClient.TrackEvent("Creator is on Cloud");
                    string subject = this.applicationBetaGraphClient.Users[createdByUserId].Events[eventGraphId].Request().Select("subject").GetAsync().Result.Subject;
                    try
                    {
                        await this.applicationBetaGraphClient.Users[createdByUserId].Events[eventGraphId].Cancel(comment).Request().PostAsync();
                        telemetryClient.TrackTrace($"{subject} cancelled SUCCESS");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Cloud user cancellation FAIL. {ex.Message}"));
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"CancelEventAsync FAIL {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Create teams event.
        /// </summary>
        /// <param name="eventEntity">Event details from user for which event needs to be created.</param>
        /// /// <param name="telemetryClient">telemetry</param>
        /// <returns>Created event details.</returns>
        public async Task<Event> CreateEventAsync(EventEntity eventEntity, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("CreateEventAsync CALLED");
                eventEntity = eventEntity ?? throw new ArgumentNullException(nameof(eventEntity), "Event details cannot be null");

                var teamsEvent = new Event { };
                ExchangeService service;

                var user = await this.delegatedGraphClient.Me.Request().GetAsync();
                string userPrincipal = user.UserPrincipalName;

                try
                {
                    teamsEvent.Subject = eventEntity.Name;
                    teamsEvent.Body = new ItemBody
                    {
                        ContentType = Microsoft.Graph.BodyType.Html,
                        Content = this.GetEventBodyContent(eventEntity, telemetryClient),
                    };
                    teamsEvent.Attendees = eventEntity.IsAutoRegister && eventEntity.Audience == (int)EventAudience.Private ?
                                await this.GetEventAttendeesTemplateAsync(eventEntity, telemetryClient) :
                                new List<Microsoft.Graph.Attendee>();
                    teamsEvent.OnlineMeetingUrl = eventEntity.Type == (int)EventType.LiveEvent ? eventEntity.MeetingLink : null;
                    teamsEvent.IsReminderOn = true;
                    teamsEvent.Location = eventEntity.Type == (int)EventType.InPerson ? new Location
                    {
                        DisplayName = eventEntity.Venue,
                    }
                    : null;
                    teamsEvent.AllowNewTimeProposals = false;
                    teamsEvent.IsOnlineMeeting = eventEntity.Type == (int)EventType.Teams;
                    teamsEvent.OnlineMeetingProvider = eventEntity.Type == (int)EventType.Teams ? OnlineMeetingProviderType.TeamsForBusiness : OnlineMeetingProviderType.Unknown;
                    teamsEvent.Start = new DateTimeTimeZone
                    {
                        DateTime = eventEntity.StartDate?.ToString("s", CultureInfo.InvariantCulture),
                        TimeZone = TimeZoneInfo.Utc.Id,
                    };
                    teamsEvent.End = new DateTimeTimeZone
                    {
                        DateTime = eventEntity.StartDate.Value.Date.Add(
                        new TimeSpan(eventEntity.EndTime.Hour, eventEntity.EndTime.Minute, eventEntity.EndTime.Second)).ToString("s", CultureInfo.InvariantCulture),
                        TimeZone = TimeZoneInfo.Utc.Id,
                    };
                    if (eventEntity.NumberOfOccurrences > 1)
                    {
                        // Create recurring event.
                        teamsEvent = this.GetRecurringEventTemplate(teamsEvent, eventEntity, telemetryClient);
                    }

                    telemetryClient.TrackEvent("Event base creation SUCCESS");
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Event base creation FAIL {ex.Message}"));
                    return null;
                }

                if (this.isOnPremUser)
                {
                    telemetryClient.TrackEvent("User is OnPrem");

                    string myDecodedString;

                    if (eventEntity.Type == (int)EventType.Teams)
                    {
                        telemetryClient.TrackEvent("Creating event type TEAMS");
                        try
                        {
                            var onlineMeeting = new OnlineMeeting
                            {
                                StartDateTime = DateTimeOffset.Parse(teamsEvent.Start.DateTime, CultureInfo.InvariantCulture),
                                EndDateTime = DateTimeOffset.Parse(teamsEvent.End.DateTime, CultureInfo.InvariantCulture),
                                Subject = "User Token Meeting",
                            };

                            var meeting = await this.delegatedGraphClient.Me.OnlineMeetings.Request().AddAsync(onlineMeeting);
                            myDecodedString = HttpUtility.UrlDecode(meeting.JoinInformation.Content);
                            myDecodedString = myDecodedString.Remove(0, 15);
                            myDecodedString = teamsEvent.Body.Content + " " + myDecodedString;

                            telemetryClient.TrackEvent("String decoding SUCCESS");
                        }
                        catch (Exception ex)
                        {
                            telemetryClient.TrackException(new Exception($"String decoding FAIL {ex.Message}"));
                            return null;
                        }
                    }
                    else
                    {
                        if (eventEntity.Type == (int)EventType.InPerson)
                        {
                            telemetryClient.TrackEvent("Creating event type InPerson");
                        }

                        if (eventEntity.Type == (int)EventType.LiveEvent)
                        {
                            telemetryClient.TrackEvent("Creating event type LiveEvent");
                        }

                        try
                        {
                            myDecodedString = teamsEvent.Body.Content;
                            telemetryClient.TrackEvent("Body content loaded SUCCESS");
                        }
                        catch (Exception ex)
                        {
                            telemetryClient.TrackException(new Exception($"Body content loading FAIL {ex.Message}"));
                            return null;
                        }
                    }

                    try
                    {
                        service = this.Service(userPrincipal, telemetryClient);
                        telemetryClient.TrackEvent("Service creation SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Service creation FAIL. {ex.Message}"));
                        return null;
                    }

                    try
                    {
                        this.EWS_CRUD_Event(telemetryClient, service, teamsEvent, myDecodedString);
                        telemetryClient.TrackTrace($"{teamsEvent.Subject} creation SUCCESS");
                        return teamsEvent;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"OnPrem event creation FAIL. {ex.Message}"));
                        return null;
                    }
                }
                else
                {
                    telemetryClient.TrackEvent("User is on Cloud");
                    try
                    {
                        var cloudEvent = await this.delegatedGraphClient.Me.Events.Request().Header("Prefer", $"outlook.timezone=\"{TimeZoneInfo.Utc.Id}\"").AddAsync(teamsEvent);
                        telemetryClient.TrackTrace($"{teamsEvent.Subject} creation SUCCESS");
                        return cloudEvent;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Cloud event creation FAIL. {ex.Message}"));
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"CreateEventAsync FAIL. {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Update teams event.
        /// </summary>
        /// <param name="eventEntity">Event details from user for which event needs to be updated.</param>
        /// <param name="telemetryClient">telemetry</param>
        /// <returns>Updated event details.</returns>
        public async Task<Event> UpdateEventAsync(EventEntity eventEntity, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("UpdateEventAsync CALLED");
                eventEntity = eventEntity ?? throw new ArgumentNullException(nameof(eventEntity), "Event details cannot be null");

                ItemId eventId = eventEntity.GraphEventId;
                var teamsEvent = new Event { };
                ExchangeService service;

                bool isCreatedByOnPremUser = this.delegatedGraphClient.Users[eventEntity.CreatedBy].Request().Select("onPremisesSyncEnabled").GetAsync().Result.OnPremisesSyncEnabled.HasValue;

                var user = await this.delegatedGraphClient.Users[eventEntity.CreatedBy].Request().GetAsync();
                string userPrincipal = user.UserPrincipalName;

                try
                {
                    teamsEvent.Subject = eventEntity.Name;
                    teamsEvent.Body = new ItemBody
                    {
                        ContentType = Microsoft.Graph.BodyType.Html,
                        Content = this.GetEventBodyContent(eventEntity, telemetryClient),
                    };
                    teamsEvent.Attendees = await this.GetEventAttendeesTemplateAsync(eventEntity, telemetryClient);
                    teamsEvent.OnlineMeetingUrl = eventEntity.Type == (int)EventType.LiveEvent ? eventEntity.MeetingLink : null;
                    teamsEvent.IsReminderOn = true;
                    teamsEvent.Location = eventEntity.Type == (int)EventType.InPerson ? new Location
                    {
                        DisplayName = eventEntity.Venue,
                    }
                    : null;
                    teamsEvent.AllowNewTimeProposals = false;
                    teamsEvent.IsOnlineMeeting = eventEntity.Type == (int)EventType.Teams;
                    teamsEvent.OnlineMeetingProvider = eventEntity.Type == (int)EventType.Teams ? OnlineMeetingProviderType.TeamsForBusiness : OnlineMeetingProviderType.Unknown;
                    teamsEvent.Start = new DateTimeTimeZone
                    {
                        DateTime = eventEntity.StartDate?.ToString("s", CultureInfo.InvariantCulture),
                        TimeZone = TimeZoneInfo.Utc.Id,
                    };
                    teamsEvent.End = new DateTimeTimeZone
                    {
                        DateTime = eventEntity.StartDate.Value.Date.Add(
                        new TimeSpan(eventEntity.EndTime.Hour, eventEntity.EndTime.Minute, eventEntity.EndTime.Second)).ToString("s", CultureInfo.InvariantCulture),
                        TimeZone = TimeZoneInfo.Utc.Id,
                    };
                    if (eventEntity.NumberOfOccurrences > 1)
                    {
                        teamsEvent = this.GetRecurringEventTemplate(teamsEvent, eventEntity, telemetryClient);
                    }

                    telemetryClient.TrackEvent("Event base update SUCCESS");
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Event base update FAIL {ex.Message}"));
                    return null;
                }

                if (isCreatedByOnPremUser)
                {
                    telemetryClient.TrackEvent("Creator is OnPrem");

                    try
                    {
                        service = this.Service(userPrincipal, telemetryClient);
                        telemetryClient.TrackEvent("Service creation SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Service creation FAIL. {ex.Message}"));
                        return null;
                    }

                    try
                    {
                        this.EWS_CRUD_Event(telemetryClient, service, teamsEvent, eventId);
                        telemetryClient.TrackTrace($"{teamsEvent.Subject} update SUCCESS");
                        return teamsEvent;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"OnPrem event update FAIL. {ex.Message}"));
                        return null;
                    }
                }
                else
                {
                    telemetryClient.TrackEvent("User is on Cloud");
                    try
                    {
                        var cloudEvent = await this.applicationGraphClient.Users[eventEntity.CreatedBy].Events[eventEntity.GraphEventId].Request().Header("Prefer", $"outlook.timezone=\"{TimeZoneInfo.Utc.Id}\"").UpdateAsync(teamsEvent);
                        telemetryClient.TrackTrace($"{teamsEvent.Subject} update SUCCESS");
                        return cloudEvent;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Cloud event update FAIL. {ex.Message}"));
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"UpdateEventAsync FAIL. {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Modify event details for recurring event creation.
        /// </summary>
        /// <param name="teamsEvent">Event details which will be sent to Graph API.</param>
        /// <param name="eventEntity">Event details from user for which event needs to be created.</param>
        /// <param name="telemetryClient">Telementry.</param>
        /// <returns>Event details to be sent to Graph API.</returns>
        private Event GetRecurringEventTemplate(Event teamsEvent, EventEntity eventEntity, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("GetRecurringEventTemplate CALLED");

                // Create recurring event.
                teamsEvent.Recurrence = new PatternedRecurrence
                {
                    Pattern = new RecurrencePattern
                    {
                        Type = RecurrencePatternType.Daily,
                        Interval = 1,
                    },
                    Range = new RecurrenceRange
                    {
                        Type = RecurrenceRangeType.EndDate,
                        EndDate = new Date((int)eventEntity.EndDate?.Year, (int)eventEntity.EndDate?.Month, (int)eventEntity.EndDate?.Day),
                        StartDate = new Date((int)eventEntity.StartDate?.Year, (int)eventEntity.StartDate?.Month, (int)eventEntity.StartDate?.Day),
                    },
                };
                telemetryClient.TrackTrace("GetRecurringEventTemplate SUCCESS");
                return teamsEvent;
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"GetRecurringEventTemplate FAIL {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Get list of event attendees for creating teams event.
        /// </summary>
        /// <param name="eventEntity">Event details containing registered attendees.</param>
        /// <param name="telemetryClient">Telementry.</param>
        /// <returns>List of attendees.</returns>
        private async Task<List<Microsoft.Graph.Attendee>> GetEventAttendeesTemplateAsync(EventEntity eventEntity, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("GetEventAttendeesTemplateAsync CALLED");

                var attendees = new List<Microsoft.Graph.Attendee>();

                if (string.IsNullOrEmpty(eventEntity.RegisteredAttendees) && string.IsNullOrEmpty(eventEntity.AutoRegisteredAttendees))
                {
                    telemetryClient.TrackTrace("RegisteredAttendees & AutoRegisteredAttendees NULL");
                    return attendees;
                }

                try
                {
                    if (!string.IsNullOrEmpty(eventEntity.RegisteredAttendees))
                    {
                        var registeredAttendeesList = eventEntity.RegisteredAttendees.Trim().Split(";");

                        if (registeredAttendeesList.Any())
                        {
                            var userProfiles = await this.userGraphHelper.GetUsersAsync(registeredAttendeesList);

                            foreach (var userProfile in userProfiles)
                            {
                                attendees.Add(new Microsoft.Graph.Attendee
                                {
                                    EmailAddress = new Microsoft.Graph.EmailAddress
                                    {
                                        Address = userProfile.UserPrincipalName,
                                        Name = userProfile.DisplayName,
                                    },
                                    Type = AttendeeType.Required,
                                });
                            }
                        }
                    }

                    telemetryClient.TrackTrace("Finding RegisteredAttendees SUCCESS");
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Finding RegisteredAttendees FAIL {ex.Message}"));
                    return null;
                }

                try
                {
                    if (!string.IsNullOrEmpty(eventEntity.AutoRegisteredAttendees))
                    {
                        var autoRegisteredAttendeesList = eventEntity.AutoRegisteredAttendees.Trim().Split(";");

                        if (autoRegisteredAttendeesList.Any())
                        {
                            var userProfiles = await this.userGraphHelper.GetUsersAsync(autoRegisteredAttendeesList);

                            foreach (var userProfile in userProfiles)
                            {
                                attendees.Add(new Microsoft.Graph.Attendee
                                {
                                    EmailAddress = new Microsoft.Graph.EmailAddress
                                    {
                                        Address = userProfile.UserPrincipalName,
                                        Name = userProfile.DisplayName,
                                    },
                                    Type = AttendeeType.Required,
                                });
                            }
                        }
                    }

                    telemetryClient.TrackTrace("Finding AutoRegisteredAttendees SUCCESS");
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Finding AutoRegisteredAttendees FAIL {ex.Message}"));
                    return null;
                }

                telemetryClient.TrackTrace("GetEventAttendeesTemplateAsync  SUCCESS");
                return attendees;
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"GetEventAttendeesTemplateAsync FAIL {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Get the event body content based on event type
        /// </summary>
        /// <param name="eventEntity">The event details</param>
        /// <param name="telemetryClient">Telementry.</param>
        /// <returns>Returns </returns>
        private string GetEventBodyContent(EventEntity eventEntity, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("GetEventBodyContent CALLED");
                switch ((EventType)eventEntity.Type)
                {
                    case EventType.InPerson:
                        return HttpUtility.HtmlEncode(eventEntity.Description);

                    case EventType.LiveEvent:
                        return $"{HttpUtility.HtmlEncode(eventEntity.Description)}<br/><br/>{this.localizer.GetString("CalendarEventLiveEventURLText", $"<a href='{eventEntity.MeetingLink}'>{eventEntity.MeetingLink}</a>")}";

                    default:
                        return HttpUtility.HtmlEncode(eventEntity.Description);
                }
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"GetEventBodyContent FAIL {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Create teams service.
        /// </summary>
        /// <param name="userPrincipal">Email ID of the user that is currently logged in.</param>
        /// <param name="telemetryClient">Telementry.</param>
        /// <returns>Created service.</returns>
        private ExchangeService Service(string userPrincipal, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("Service function CALLED");

                var ewsClient = new ExchangeService(ExchangeVersion.Exchange2013_SP1);
                ewsClient.Credentials = new WebCredentials(this.serviceEmail, this.servicePass);
                ewsClient.ImpersonatedUserId = new ImpersonatedUserId(ConnectingIdType.SmtpAddress, userPrincipal);
                ewsClient.Url = new Uri("https://mail.qatartest309.com/EWS/Exchange.asmx");

                telemetryClient.TrackTrace("Service creation SUCCESS");
                return ewsClient;
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"Service function FAIL {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Create teams event.
        /// </summary>
        /// <param name="telemetryClient">Telementry.</param>
        /// <param name="service">Exchange service that will be used to create event.</param>
        /// <param name="teamsEvent">Details that need to be filled in the event.</param>
        /// <param name="body">Body of the event.</param>
        /// <returns>Id of the event created</returns>
        private ItemId EWS_CRUD_Event(TelemetryClient telemetryClient, ExchangeService service, Event teamsEvent, string body)
        {
            try
            {
                telemetryClient.TrackTrace("EWS_Event_Creation CALLED");
                Appointment appointment;

                CreateUpdate createEvent = CreateUpdate.CreateAppointment;
                try
                {
                    appointment = this.TeamAppointment(teamsEvent, createEvent, service, body, telemetryClient);
                    telemetryClient.TrackEvent("Appointment creation SUCCESS");
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Appointment creation FAIL. {ex.Message}"));
                    return null;
                }

                try
                {
                    Item item = Item.Bind(service, appointment.Id, new PropertySet(ItemSchema.Subject));
                    teamsEvent.Id = item.Id.ToString();

                    ItemId eventId = appointment.Id;
                    telemetryClient.TrackTrace("EWS_Event_Creation SUCCESS");
                    return eventId;
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Item binding FAIL. {ex.Message}"));
                    return null;
                }
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"EWS_Event_Creation FAIL. {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Updates the event.
        /// </summary>
        /// <param name="telemetryClient">Telementry.</param>
        /// <param name="service">Exchange service that will be used to update event.</param>
        /// <param name="teamsEvent">Details that will updated in the event</param>
        /// <param name="eventId">Id of the event that need to me modified.</param>
        private void EWS_CRUD_Event(TelemetryClient telemetryClient, ExchangeService service, Event teamsEvent, ItemId eventId)
        {
            telemetryClient.TrackTrace("EWS_Event_Update CALLED");
            CreateUpdate updateEvent = CreateUpdate.UpdateAppointment;

            try
            {
                this.TeamAppointment(teamsEvent, updateEvent, service, eventId.ToString(), telemetryClient);
                telemetryClient.TrackTrace("EWS_Event_Update SUCCESS");
            }
            catch (Exception ex)
            {
                telemetryClient.TrackException(new Exception($"EWS_Event_Update FAIL. {ex.Message}"));
            }
        }

        /// <summary>
        /// Creates or updates an appointment.
        /// </summary>
        /// <param name="teamsEvent">Detailsof the event</param>
        /// <param name="createUpdate"> Enum to check if the appointment should be created or updated</param>
        /// <param name="service">Exchange service that will be used to delete event.</param>
        /// <param name="idOrBody"> For u[dating appointment an ID will be passed and for creating appointment body will be passed</param>
        /// <param name="telemetryClient">Telementry.</param>
        private Appointment TeamAppointment(Event teamsEvent, CreateUpdate createUpdate, ExchangeService service, string idOrBody, TelemetryClient telemetryClient)
        {
            try
            {
                telemetryClient.TrackTrace("TeamAppointment CALLED");

                if (createUpdate.Equals(CreateUpdate.CreateAppointment))
                {
                    try
                    {
                        this.EventAppointment = new Appointment(service);
                        this.EventAppointment.Body = idOrBody;
                        telemetryClient.TrackEvent("Appointment CREATE base SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Appointment CREATE base FAIL. {ex.Message}"));
                        return null;
                    }
                }
                else
                {
                    try
                    {
                        ItemId eventId = new ItemId(idOrBody);
                        this.EventAppointment = Appointment.Bind(service, eventId);
                        this.EventAppointment.Body = teamsEvent.Body.Content;
                        telemetryClient.TrackEvent("Appointment UPDATE base SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Appointment UPDATE base FAIL. {ex.Message}"));
                        return null;
                    }
                }

                try
                {
                    this.EventAppointment.Subject = teamsEvent.Subject;
                    this.EventAppointment.Body.BodyType = Exchange.WebServices.Data.BodyType.HTML;
                    this.EventAppointment.Start = DateTime.Parse(teamsEvent.Start.DateTime, CultureInfo.InvariantCulture);
                    this.EventAppointment.End = DateTime.Parse(teamsEvent.End.DateTime, CultureInfo.InvariantCulture);
                    this.EventAppointment.Location = teamsEvent.Location != null ? teamsEvent.Location.DisplayName : string.Empty;

                    try
                    {
                        foreach (var attendee in teamsEvent.Attendees)
                        {
                            if (attendee.Type == 0)
                            {
                                this.EventAppointment.RequiredAttendees.Add(attendee.EmailAddress.Address);
                            }
                            else
                            {
                                this.EventAppointment.OptionalAttendees.Add(attendee.EmailAddress.Address);
                            }
                        }

                        telemetryClient.TrackEvent("Event attendees SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Event attendees FAIL. {ex.Message}"));
                        return null;
                    }

                    this.EventAppointment.ReminderDueBy = DateTime.Now;
                    telemetryClient.TrackEvent("Appointment data SUCCESS");
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(new Exception($"Appointment data FAIL. {ex.Message}"));
                    return null;
                }

                if (createUpdate.Equals(CreateUpdate.CreateAppointment))
                {
                    try
                    {
                        this.EventAppointment.Save(SendInvitationsMode.SendToAllAndSaveCopy);
                        telemetryClient.TrackTrace($"TeamAppointment CREATE SUCCESS");
                        return this.EventAppointment;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Appointment CREATE FAIL. {ex.Message}"));
                        return null;
                    }
                }
                else
                {
                    try
                    {
                        SendInvitationsOrCancellationsMode mode = this.EventAppointment.IsMeeting ?
                        SendInvitationsOrCancellationsMode.SendToAllAndSaveCopy : SendInvitationsOrCancellationsMode.SendToNone;

                        this.EventAppointment.Update(ConflictResolutionMode.AlwaysOverwrite);
                        telemetryClient.TrackTrace($"TeamAppointment UPDATE SUCCESS");
                        return this.EventAppointment;
                    }
                    catch (Exception ex)
                    {
                        telemetryClient.TrackException(new Exception($"Appointment UPDATE FAIL. {ex.Message}"));
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                telemetryClient.TrackEvent($"TeamAppointment FAIL. {ex.Message}");
                return null;
            }
        }
    }
}
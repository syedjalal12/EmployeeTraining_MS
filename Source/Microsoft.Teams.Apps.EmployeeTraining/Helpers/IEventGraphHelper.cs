// <copyright file="IEventGraphHelper.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Microsoft.Teams.Apps.EmployeeTraining.Helpers
{
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights;
    using Microsoft.Graph;
    using Microsoft.Teams.Apps.EmployeeTraining.Models;

    /// <summary>
    /// Provides helper methods to make Microsoft Graph API calls related to managing events.
    /// </summary>
    public interface IEventGraphHelper
    {
        /// <summary>
        /// Create teams event.
        /// </summary>
        /// <param name="eventEntity">Event details from user for which event needs to be created.</param>
        /// <param name="telemetryClient">telemetry</param>
        /// <returns>Created event details.</returns>
        Task<Event> CreateEventAsync(EventEntity eventEntity, TelemetryClient telemetryClient);

        /// <summary>
        /// Update teams event.
        /// </summary>
        /// <param name="eventEntity">Event details from user for which event needs to be updated.</param>
        /// <param name="telemetryClient">telemetry</param>
        /// <returns>Updated event details.</returns>
        Task<Event> UpdateEventAsync(EventEntity eventEntity, TelemetryClient telemetryClient);

        /// <summary>
        /// Cancel calendar event.
        /// </summary>
        /// <param name="eventGraphId">Event Id received from Graph.</param>
        /// <param name="createdByUserId">User Id who created event.</param>
        /// <param name="comment">Cancellation comment.</param>
        /// /// <param name="telemetryClient">telemetry</param>
        /// <returns>True if event cancellation is successful.</returns>
        Task<bool> CancelEventAsync(string eventGraphId, string createdByUserId, string comment, TelemetryClient telemetryClient);
    }
}

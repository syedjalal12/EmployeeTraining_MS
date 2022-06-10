// <copyright file="AzureVaultSettings.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>
namespace Microsoft.Teams.Apps.EmployeeTraining.Models.Configuration
{
    /// <summary>
    /// A class which helps to provide Azure Vault settings for application.
    /// </summary>
    public class AzureVaultSettings
    {
        /// <summary>
        /// Gets or sets Service Email of application.
        /// </summary>
        public string ServiceEmail { get; set; }

        /// <summary>
        /// Gets or sets Service Password of application.
        /// </summary>
        public string ServicePassword { get; set; }
    }
}

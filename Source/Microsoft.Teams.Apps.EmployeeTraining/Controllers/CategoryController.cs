// <copyright file="CategoryController.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Microsoft.Teams.Apps.EmployeeTraining.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Teams.Apps.EmployeeTraining.Authentication;
    using Microsoft.Teams.Apps.EmployeeTraining.Helpers;
    using Microsoft.Teams.Apps.EmployeeTraining.Models;
    using Microsoft.Teams.Apps.EmployeeTraining.Repositories;

    /// <summary>
    /// The controller handles the data requests related to categories.
    /// </summary>
    [Route("api/category")]
    [ApiController]
    public class CategoryController : BaseController
    {
        /// <summary>
        /// Logs errors and information.
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// Provides the methods for event category operations on storage.
        /// </summary>
        private readonly ICategoryRepository categoryStorageProvider;

        /// <summary>
        /// Provides the helper methods for managing categories.
        /// </summary>
        private readonly ICategoryHelper categoryHelper;

        /// <summary>
        /// Logs in app insight.
        /// </summary>
        private TelemetryClient telemetryClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="CategoryController"/> class.
        /// </summary>
        /// <param name="logger">The ILogger object which logs errors and information.</param>
        /// <param name="telemetryClient">The Application Insights telemetry client.</param>
        /// <param name="categoryStorageProvider">The category storage provider dependency injection.</param>
        /// <param name="categoryHelper">The category helper dependency injection.</param>
        public CategoryController(
            ILogger<CategoryController> logger,
            TelemetryClient telemetryClient,
            ICategoryRepository categoryStorageProvider,
            ICategoryHelper categoryHelper)
            : base(telemetryClient)
        {
            this.logger = logger;
            this.categoryStorageProvider = categoryStorageProvider;
            this.categoryHelper = categoryHelper;
            this.telemetryClient = telemetryClient;
        }

        /// <summary>
        /// The HTTP GET call to get all event categories.
        /// </summary>
        /// <returns>Returns the list of categories sorted by category name if request processed successfully. Else, it throws an exception.</returns>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetCategoriesAsync()
        {
            this.telemetryClient.TrackTrace("GetCategoriesAsync CALLED");

            this.RecordEvent("Get all categories- The HTTP call to GET all categories has been initiated");

            try
            {
                var categories = await this.categoryStorageProvider.GetCategoriesAsync();

                this.RecordEvent("Get all categories- The HTTP call to GET all categories succeeded");
                this.telemetryClient.TrackEvent("Get all categories- The HTTP call to GET all categories succeeded");

                if (categories.IsNullOrEmpty())
                {
                    this.telemetryClient.TrackTrace("Categories are not available");
                    this.logger.LogInformation("Categories are not available");
                    return this.Ok(new List<Category>());
                }

                OkObjectResult orederedCategories;

                try
                {
                    await this.categoryHelper.CheckIfCategoryIsInUseAsync(categories);
                    orederedCategories = this.Ok(categories.OrderBy(category => category.Name));

                    this.telemetryClient.TrackTrace("GetCategoriesAsync SUCCESS");
                    return orederedCategories;
                }
                catch (Exception ex)
                {
                    this.telemetryClient.TrackException(new Exception($"GetCategoriesAsync FAIL {ex.Message}"));
                    return null;
                }
            }
            catch (Exception ex)
            {
                this.telemetryClient.TrackException(new Exception($"GetCategoriesAsync FAIL {ex.Message}"));

                this.RecordEvent("Get all categories- The HTTP call to GET all categories has been failed");
                this.logger.LogError(ex, "Error occurred while fetching all categories");
                throw;
            }
        }

        /// <summary>
        /// The HTTP GET call to get all event categories.
        /// </summary>
        /// <returns>Returns the list of categories sorted by category name if request processed successfully. Else, it throws an exception.</returns>
        [Authorize]
        [HttpGet("get-categories-for-event")]
        public async Task<IActionResult> GetCategoriesToCreateEventAsync()
        {
            this.telemetryClient.TrackTrace("GetCategoriesToCreateEventAsync CALLED");
            this.RecordEvent("Get all categories- The HTTP call to GET all categories has been initiated");

            try
            {
                IEnumerable<Category> categories;

                try
                {
                    this.telemetryClient.TrackEvent("Getting categories");
                    categories = await this.categoryStorageProvider.GetCategoriesAsync();
                    this.telemetryClient.TrackEvent("Getting categories SUCCESS");

                    try
                    {
                        if (categories.IsNullOrEmpty())
                        {
                            this.telemetryClient.TrackTrace("NO CATEGORY FOUND");
                            this.logger.LogInformation("Categories are not available");
                            return this.Ok(new List<Category>());
                        }

                        this.RecordEvent("Get all categories- The HTTP call to GET all categories succeeded");
                        this.telemetryClient.TrackTrace("GetCategoriesToCreateEventAsync SUCCESS");
                        return this.Ok(categories.OrderBy(category => category.Name));
                    }
                    catch (Exception ex)
                    {
                        this.telemetryClient.TrackException(new Exception($"GetCategoriesToCreateEventAsync FAIL {ex.Message}"));
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    this.telemetryClient.TrackException(new Exception($"Getting categories FAIL {ex.Message}"));
                    return null;
                }
            }
            catch (Exception ex)
            {
                this.RecordEvent("Get all categories- The HTTP call to GET all categories has been failed");
                this.logger.LogError(ex, "Error occurred while fetching all categories");
                throw;
            }
        }

        /// <summary>
        /// The HTTP POST call to create a new category.
        /// </summary>
        /// <param name="categoryDetails">The category details that needs to be created.</param>
        /// <param name="teamId">The LnD team Id.</param>
        /// <returns>Returns true in case if category created successfully. Else returns false.</returns>
        [Authorize(PolicyNames.MustBeLnDTeamMemberPolicy)]
        [HttpPost]
        public async Task<IActionResult> CreateCategoryAsync([FromBody] Category categoryDetails, string teamId)
        {
            this.telemetryClient.TrackTrace("CreateCategoryAsync CALLED");

            if (string.IsNullOrEmpty(teamId))
            {
                this.telemetryClient.TrackException(new Exception($"TeamID is null or empty"));

                this.logger.LogError("TeamId is either null or empty");
                return this.BadRequest(new ErrorResponse { Message = "Team Id is either null or empty" });
            }

            if (categoryDetails == null)
            {
                this.telemetryClient.TrackException(new Exception($"The category details must be provided"));

                this.logger.LogError("The category details must be provided");
                return this.BadRequest(new ErrorResponse { Message = "The category details must be provided" });
            }

            Category category = new Category { };
            try
            {
#pragma warning disable CA1062 // Null check is handled by data annotations at model level
                category.CategoryId = Convert.ToString(Guid.NewGuid(), CultureInfo.InvariantCulture);
#pragma warning restore CA1062 // Null check is handled by data annotations at model level
                category.Name = categoryDetails.Name.Trim();
                category.Description = categoryDetails.Description.Trim();
                category.CreatedBy = this.UserAadId;
                category.CreatedOn = DateTime.UtcNow;
                category.UpdatedOn = DateTime.UtcNow;

                this.telemetryClient.TrackEvent("Category base SUCCESSS");
            }
            catch (Exception ex)
            {
                this.telemetryClient.TrackException(new Exception($"Category base FAIL {ex.Message}"));
            }

            this.RecordEvent("Create category- The HTTP POST call to create a category has been initiated");

            try
            {
                var isCategoryCreated = await this.categoryStorageProvider.UpsertCategoryAsync(category);
                if (isCategoryCreated)
                {
                    this.telemetryClient.TrackTrace("CreateCategoryAsync SUCCESSS");
                }
                else
                {
                    this.telemetryClient.TrackException(new Exception("CreateCategoryAsync FAIL"));
                }

                this.RecordEvent("Create category- The HTTP POST call to create a category has succeeded");

                return this.Ok(isCategoryCreated);
            }
            catch (Exception ex)
            {
                this.RecordEvent("Create category- The HTTP POST call to create a category has been failed");
                this.logger.LogError(ex, "Error occurred while creating a category");
                throw;
            }
        }

        /// <summary>
        /// The HTTP PATCH call to update a category.
        /// </summary>
        /// <param name="categoryDetails">The category details that needs to be updated.</param>
        /// <param name="teamId">The LnD team Id.</param>
        /// <returns>Returns true in case if category updated successfully. Else returns false.</returns>
        [Authorize(PolicyNames.MustBeLnDTeamMemberPolicy)]
        [HttpPatch]
        public async Task<IActionResult> UpdateCategoryAsync([FromBody] Category categoryDetails, string teamId)
        {
            this.telemetryClient.TrackTrace("UpdateCategoryAsync CALLED");

            this.RecordEvent("Update category- The HTTP PATCH call to update a category has been initiated");

            if (string.IsNullOrEmpty(teamId))
            {
                this.telemetryClient.TrackException(new Exception("TeamID cant be null"));
                this.logger.LogError("Team Id is either null or empty");
                this.RecordEvent("Update category- The HTTP PATCH call to update a category has been initiated");
                return this.BadRequest(new ErrorResponse { Message = "Team Id is either null or empty" });
            }

            try
            {
                this.telemetryClient.TrackEvent("Finding telementry client");
#pragma warning disable CA1062 // Null check is handled by data annotations at model level
                var categoryData = await this.categoryStorageProvider.GetCategoryAsync(categoryDetails.CategoryId);
#pragma warning restore CA1062 // Null check is handled by data annotations at model level

                if (categoryData == null)
                {
                    string exceptionText = CultureInfo.InvariantCulture.ToString() + $" Update category- The HTTP PATCH call to update a category has failed since the category Id {categoryDetails.CategoryId} was not found for the team Id {teamId} and user Id {this.UserAadId}";

                    this.telemetryClient.TrackException(new Exception("UpdateCategoryAsync FAIL"));
                    this.telemetryClient.TrackException(new Exception(exceptionText));
                    this.RecordEvent(string.Format(CultureInfo.InvariantCulture, "Update category- The HTTP PATCH call to update a category has failed since the category Id {0} was not found for the team Id {1} and user Id {2}", categoryDetails.CategoryId, teamId, this.UserAadId));
                    return this.Ok(false);
                }

                try
                {
                    categoryData.Name = categoryDetails.Name;
                    categoryData.Description = categoryDetails.Description;
                    categoryData.UpdatedBy = this.UserAadId;
                    categoryData.UpdatedOn = DateTime.UtcNow;

                    this.telemetryClient.TrackEvent("Category base SUCCESS");
                }
                catch (Exception ex)
                {
                    this.telemetryClient.TrackEvent($"Category base FAIL {ex.Message}");
                }

                var isCategoryUpdated = await this.categoryStorageProvider.UpsertCategoryAsync(categoryData);

                if (!isCategoryUpdated)
                {
                    this.telemetryClient.TrackEvent($"isCategoryUpdated FAIL");
                    this.RecordEvent("Update category- The category update was unsuccessful");
                }

                try
                {
                    this.RecordEvent("Update category- The category has been updated successfully");
                    var okResult = this.Ok(isCategoryUpdated);
                    return okResult;
                }
                catch (Exception ex)
                {
                    this.telemetryClient.TrackException(new Exception($"UpdateCategoryAsync FAIL {ex}"));
                    return null;
                }
            }
            catch (Exception ex)
            {
                this.RecordEvent("Update category- The HTTP PATCH call to update a category has been failed");
                this.logger.LogError(ex, "Error occurred while updating a category");
                throw;
            }
        }

        /// <summary>
        /// The HTTP DELETE call to delete the categories.
        /// </summary>
        /// <param name="teamId">The team Id from which categories need to be deleted.</param>
        /// <param name="categoryIds">The comma separated category Ids to be deleted.</param>
        /// <returns>Returns true if categories deleted successfully. Else returns false.</returns>
        [Authorize(PolicyNames.MustBeLnDTeamMemberPolicy)]
        [HttpDelete]
        public async Task<IActionResult> DeleteCategoriesAsync(string teamId, string categoryIds)
        {
            this.telemetryClient.TrackTrace("DeleteCategoriesAsync CALLED");

            if (string.IsNullOrEmpty(teamId))
            {
                this.telemetryClient.TrackException(new Exception("TheamID cant be empty"));
                this.logger.LogError("Team Id is either null or empty");
                return this.BadRequest(new ErrorResponse { Message = "Team Id is either null or empty" });
            }

            if (string.IsNullOrEmpty(categoryIds))
            {
                this.telemetryClient.TrackException(new Exception("categoryIds cant be empty"));
                this.logger.LogError("String containing category Ids is either null or empty");
                return this.BadRequest(new ErrorResponse { Message = "String containing category Ids is either null or empty" });
            }

            this.RecordEvent("Delete categories- The HTTP call to delete categories has been initiated");

            try
            {
                var categoriesList = categoryIds.Split(",");
                var categories = categoriesList.Select(categoryId => new Category { CategoryId = categoryId }).ToList();
                this.telemetryClient.TrackEvent("Getting category SUCCESS");

                await this.categoryHelper.CheckIfCategoryIsInUseAsync(categories);
                this.telemetryClient.TrackEvent("Checking if category is in use SUCCESS");

                var categoriesNotInUse = categories.Where(category => !category.IsInUse);
                this.telemetryClient.TrackEvent("Finding categories not in use SUCCESS");

                if (categoriesNotInUse != null && categoriesNotInUse.Any())
                {
                    this.telemetryClient.TrackEvent("Deleting categories not in use");
                    var updatedCategories = await this.categoryStorageProvider.GetCategoriesByIdsAsync(categoriesNotInUse.Select(category => category.CategoryId).ToArray());

                    this.telemetryClient.TrackEvent("updatedCategories SUCCESS");

                    bool isDeleteSuccessful = false;
                    try
                    {
                        isDeleteSuccessful = await this.categoryStorageProvider.DeleteCategoriesInBatchAsync(updatedCategories);
                        this.telemetryClient.TrackTrace("DeleteCategoriesAsync SUCCESS");
                        this.RecordEvent("Delete categories- The categories has been deleted successfully");
                    }
                    catch (Exception ex)
                    {
                        this.telemetryClient.TrackException(new Exception($"DeleteCategoriesAsync FAIL {ex.Message}"));
                        this.RecordEvent("Delete categories- The delete categories operation was unsuccessful");
                    }

                    return this.Ok(isDeleteSuccessful);
                }

                this.telemetryClient.TrackException(new Exception($"DeleteCategoriesAsync FAIL"));
                return this.Ok(false);
            }
            catch (Exception ex)
            {
                this.telemetryClient.TrackException(new Exception($"DeleteCategoriesAsync FAIL"));
                this.RecordEvent("Delete categories- The HTTP call to delete categories has been failed");
                this.logger.LogError(ex, "Error occurred while deleting categories");
                throw;
            }
        }
    }
}

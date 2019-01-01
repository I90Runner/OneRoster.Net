﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OneRosterSync.Net.Data;
using OneRosterSync.Net.Extensions;
using OneRosterSync.Net.Models;
using OneRosterSync.Net.Utils;

namespace OneRosterSync.Net.Processing
{
    public class RosterProcessor
    {
        private readonly IServiceProvider Services;
        private readonly ILogger Logger;

        public RosterProcessor(
            IServiceProvider services,
            ILogger logger)
        {
            Services = services;
            Logger = logger;
        }

        /// <summary>
        /// Process a district's OneRoster CSV feed
        /// </summary>
        /// <param name="districtId">District Id</param>
        /// <param name="cancellationToken">Token to cancel operation (not currently used)</param>
        public async Task Process(int districtId, CancellationToken cancellationToken)
        {
            using (var scope = Services.CreateScope())
            {
                using (var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
                {
                    DistrictRepo repo = new DistrictRepo(Logger, db, districtId);
                    District district = repo.District;
                    ProcessingAction action = district.ProcessingAction;
                    district.ProcessingAction = ProcessingAction.None; // clear the action out
                    district.Touch();
                    await repo.Committer.Invoke();

                    switch (district.ProcessingStatus)
                    {
                        case ProcessingStatus.Loading:
                        case ProcessingStatus.Applying:
                        case ProcessingStatus.Analyzing:
                            // already in process, bail out?
                            Logger.Here().LogError($"Unexpected Processing status {district.ProcessingStatus} for District {district.Name} ({district.DistrictId})");
                            break;

                        default:
                            break;
                    }

                    switch (action)
                    {
                        case ProcessingAction.None:
                            break;

                        case ProcessingAction.LoadSample:
                            throw new NotImplementedException();

                        case ProcessingAction.Load:
                            await Load(repo);
                            break;

                        case ProcessingAction.Analyze:
                            await Analyze(repo);
                            break;

                        case ProcessingAction.Apply:
                            await Apply(repo);
                            break;

                        case ProcessingAction.FullProcess:
                            await Load(repo);
                            await Analyze(repo);
                            await Apply(repo);
                            break;
                    }
                }
            }
        }


        /// <summary>
        /// Load the District CSV data into the database
        /// This is the first step of the Processing
        /// </summary>
        private async Task Load(DistrictRepo repo)
        {
            DataSyncHistory history = null;
            try
            {
                history = repo.PushHistory();
                repo.District.ProcessingStatus = ProcessingStatus.Loading;
                await repo.Committer.Invoke();

                var loader = new Loader(Logger, repo, repo.District.BasePath, history);

                await loader.LoadFile<CsvOrg>(@"orgs.csv");
                await loader.LoadFile<CsvCourse>(@"courses.csv");
                await loader.LoadFile<CsvAcademicSession>(@"academicSessions.csv");
                await loader.LoadFile<CsvClass>(@"classes.csv");
                await loader.LoadFile<CsvUser>(@"users.csv");
                await loader.LoadFile<CsvEnrollment>(@"enrollments.csv");
            }
            catch (Exception ex)
            {
                Logger.Here().LogError(ex, "Error Loading District Data.");
            }
            finally
            {
                repo.District.ProcessingStatus = ProcessingStatus.LoadingDone;
                repo.District.Touch();
                //history.Completed = DateTime.UtcNow;
                await repo.Committer.Invoke();
            }
        }

        /// <summary>
        /// Load the District CSV data into the database
        /// This is the first step of the Processing
        /// </summary>
        private async Task Analyze(DistrictRepo repo)
        {
            DataSyncHistory history = null;
            try
            {
                history = repo.CurrentHistory;
                repo.District.ProcessingStatus = ProcessingStatus.Loading;
                await repo.Committer.Invoke();

                var analyzer = new Analyzer(Logger, repo);
                await analyzer.MarkDeleted(history.Started);
                await analyzer.Analyze();
            }
            catch (Exception ex)
            {
                Logger.Here().LogError(ex, "Error Analyzing District Data.");
            }
            finally
            {
                repo.District.ProcessingStatus = ProcessingStatus.AnalyzingDone;
                repo.District.Touch();
                //history.Completed = DateTime.UtcNow;
                await repo.Committer.Invoke();
            }
        }


        private async Task Apply(DistrictRepo repo)
        {
            try
            {
                repo.District.ProcessingStatus = ProcessingStatus.Applying;
                repo.District.Touch();
                await repo.Committer.Invoke();

                using (var api = new ApiManager(Logger, repo.District.LmsApiEndpoint))
                {
                    var applier = new Applier(Logger, repo, api);

                    await applier.ApplyLines<CsvOrg>();
                    await applier.ApplyLines<CsvCourse>();
                    await applier.ApplyLines<CsvAcademicSession>();
                    await applier.ApplyLines<CsvClass>();
                    await applier.ApplyLines<CsvUser>();
                    await applier.ApplyLines<CsvEnrollment>();
                }
            }
            catch (Exception ex)
            {
                Logger.Here().LogError(ex, "Error Applying District Data");
                throw;
            }
            finally
            {
                repo.District.ProcessingStatus = ProcessingStatus.ApplyingDone;
                repo.District.Touch();
                await repo.Committer.Invoke();
            }
        }
    }
}
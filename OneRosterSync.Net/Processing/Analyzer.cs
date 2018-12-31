﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OneRosterSync.Net.Extensions;
using OneRosterSync.Net.Models;
using OneRosterSync.Net.Utils;

namespace OneRosterSync.Net.Processing
{
    public class Analyzer
    {
        private readonly ILogger Logger;
        private readonly DistrictRepo Repo;

        public Analyzer(ILogger logger, DistrictRepo repo)
        {
            Logger = logger;
            Repo = repo;
        }

        /// <summary>
        /// Identifies records that were missing from the feed and marks them as Deleted
        /// </summary>
        public async Task MarkDeleted(DateTime start)
        {
            foreach (var line in await Repo.Lines().Where(l => l.LastSeen < start).ToListAsync())
            {
                line.LoadStatus = LoadStatus.Deleted;
                await Repo.Committer.InvokeIfChunk();
            }
            await Repo.Committer.InvokeIfAny();
        }

        /// <summary>
        /// This is used to determine if any change needs to be pushed to the LMS
        /// Basically if a record has changed OR has never been Applied
        /// which can happen if a record is loaded and later caused to be included in the Sync
        /// </summary>
        private static bool IsUnappliedChange(DataSyncLine line) =>
            line.LoadStatus != LoadStatus.NoChange || 
            line.SyncStatus != SyncStatus.Applied;

        /// <summary>
        /// Helper to mark a record to be included in the next push to LMS
        /// </summary>
        private static void IncludeReadyTouch(DataSyncLine line)
        {
            line.IncludeInSync = true;
            line.SyncStatus = SyncStatus.ReadyToApply;
            line.Touch();
        }

        /// <summary>
        /// Analyze the records to determine which should be included in the feed
        /// based on dependencies.
        /// </summary>
        public async Task Analyze()
        {
            // load some small tables into memory for performance
            var cache = new DataLineCache(Logger);
            await cache.Load(Repo.Lines(), new[] { nameof(CsvOrg), nameof(CsvCourse), nameof(CsvClass) });

            // include all Orgs in sync by default
            foreach (var org in cache.GetMap<CsvOrg>().Values.Where(IsUnappliedChange))
                IncludeReadyTouch(org);
            await Repo.Committer.Invoke();

            // courses are manually marked for sync, so choose only those
            foreach (var course in cache.GetMap<CsvCourse>().Values.Where(l => l.IncludeInSync).Where(IsUnappliedChange))
                IncludeReadyTouch(course);
            await Repo.Committer.Invoke();

            // now walk the classes and include those which map to an included course
            var classMap = cache.GetMap<CsvClass>();
            var courseIds = cache.GetMap<CsvCourse>().Values.Where(l => l.IncludeInSync).Select(l => l.SourceId).ToHashSet();
            foreach (var _class in classMap.Values.Where(IsUnappliedChange))
            {
                CsvClass csvClass = JsonConvert.DeserializeObject<CsvClass>(_class.RawData);
                if (courseIds.Contains(csvClass.courseSourcedId))
                    IncludeReadyTouch(_class);
                await Repo.Committer.InvokeIfChunk();
            }
            await Repo.Committer.InvokeIfAny();

            // process all enrollments in the database associated with the District (in chunks of 200)
            await Repo.Lines<CsvEnrollment>().ForEachInChunksAsync(chunkSize: 200, 
                action: async (enrollment) =>
                {
                    CsvEnrollment csvEnrollment = JsonConvert.DeserializeObject<CsvEnrollment>(enrollment.RawData);

                    // figure out if we need to process this enrollment
                    if (!classMap.ContainsKey(csvEnrollment.classSourcedId) ||      // look up class associated with enrollment
                        !classMap[csvEnrollment.classSourcedId].IncludeInSync ||    // only include enrollment if the class is included
                        !IsUnappliedChange(enrollment))                             // only include if unapplied change in enrollment
                        return;

                    var user = await Repo.Lines<CsvUser>().SingleOrDefaultAsync(l => l.SourceId == csvEnrollment.userSourcedId);
                    if (user == null) // should never happen
                    {
                        Logger.Here().LogError($"Missing user for enrollment for line {enrollment.DataSyncLineId}");
                        return;
                    }

                    // mark enrollment for sync
                    IncludeReadyTouch(enrollment);

                    // mark user for sync
                    //DataSyncLine user = userMap[csvEnrollment.userSourcedId];
                    if (IsUnappliedChange(user))
                        IncludeReadyTouch(user);
                }, 
                onChunkComplete: async () => await Repo.Committer.Invoke());

            await Repo.Committer.Invoke();
        }
    }
}
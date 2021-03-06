﻿using AppDynamics.Dexter.DataObjects;
using OfficeOpenXml;
using OfficeOpenXml.ConditionalFormatting;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AppDynamics.Dexter.ProcessingSteps
{
    public class ReportIndividualApplicationAndEntityDetails : JobStepReportBase
    {
        #region Constants for Entity Metric Details Report contents

        private const string REPORT_ENTITY_DETAILS_SHEET_CONTROLLERS = "3.Controllers";

        // Metric summaries full, hourly
        private const string REPORT_ENTITY_DETAILS_SHEET_SUMMARY = "4.Calls and Response";
        // Flowmap in grid view
        private const string REPORT_ENTITY_DETAILS_SHEET_ACTIVITYGRID = "5.Activity Flow";
        // Graphs, snapshots and events all lined up in timeline
        private const string REPORT_ENTITY_DETAILS_SHEET_TIMELINE = "6.Timeline";
        // Event data 
        private const string REPORT_ENTITY_DETAILS_SHEET_EVENTS = "7.Events";
        // Snapshots
        private const string REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS = "8.Snapshots";
        // Raw metric data
        private const string REPORT_ENTITY_DETAILS_SHEET_METRICS = "9.Metric Detail";

        private const string REPORT_ENTITY_DETAILS_TABLE_TOC = "t_TOC";
        private const string REPORT_ENTITY_DETAILS_TABLE_CONTROLLERS = "t_Controllers";

        // Full and hourly metric data
        private const string REPORT_ENTITY_DETAILS_TABLE_ENTITY_FULL = "t_Metric_Summary_Full";
        private const string REPORT_ENTITY_DETAILS_TABLE_ENTITY_HOURLY = "t_Metric_Summary_Hourly";
        // Grid data
        private const string REPORT_ENTITY_DETAILS_ACTIVITY_GRID = "t_ActivityFlow";
        // Events from events.csv and hrviolationevents.csv
        private const string REPORT_ENTITY_DETAILS_TABLE_EVENTS = "t_Events";
        // Snapshot data
        private const string REPORT_ENTITY_DETAILS_TABLE_SNAPSHOTS = "t_Snapshots";
        private const string REPORT_ENTITY_DETAILS_TABLESINESS_DATA = "t_BusinessData";

        // Metric data tables from metric.values.csv
        private const string REPORT_ENTITY_DETAILS_METRIC_TABLE_METRIC_VALUES = "t_Metric_Values_{0}_{1}";

        // Hourly graph data
        private const string REPORT_ENTITY_DETAILS_METRIC_GRAPH = "g_Metrics_{0}_{1:yyyyMMddHHss}";

        private const string REPORT_ENTITY_DETAILS_PIVOT_EVENTS = "p_Events";

        private const string REPORT_ENTITY_DETAILS_PIVOT_SNAPSHOTS = "p_Snapshots";

        private const int REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT = 4;
        private const int REPORT_ENTITY_DETAILS_GRAPHS_SHEET_START_TABLE_AT = 15;
        private const int REPORT_ENTITY_DETAILS_PIVOT_SHEET_START_PIVOT_AT = 7;

        private const string REPORT_ENTITY_DETAILS_TABLE_EVENTS_IN_TIMELINE = "t_EventsTimelineHeaders";
        private const string REPORT_ENTITY_DETAILS_TABLE_SNAPSHOTS_IN_TIMELINE = "t_SnapshotsTimelineHeaders";

        // 5 minutes out of 1440 minutes (24 hours) == 0.0034722222222222
        private const double FIVE_MINUTES = 0.0034722222222222;

        #endregion

        private const string SNAPSHOT_UX_NORMAL = "NORMAL";
        private const string SNAPSHOT_UX_SLOW = "SLOW";
        private const string SNAPSHOT_UX_VERY_SLOW = "VERY_SLOW";
        private const string SNAPSHOT_UX_STALL = "STALL";
        private const string SNAPSHOT_UX_ERROR = "ERROR";

        #region Constants for metric retrieval and mapping

        // In EntityMetricExtractMapping.csv
        private const string RANGE_ROLLUP_TYPE_AVERAGE = "AVERAGE";
        private const string RANGE_ROLLUP_TYPE_SUM = "SUM";

        private const string METRIC_GRAPH_AXIS_PRIMARY = "PRIMARY";
        private const string METRIC_GRAPH_AXIS_SECONDARY = "SECONDARY";

        private const string TRANSACTIONS_METRICS_SET = "CallsAndResponse";

        #endregion

        public override bool Execute(ProgramOptions programOptions, JobConfiguration jobConfiguration)
        {
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            StepTiming stepTimingFunction = new StepTiming();
            stepTimingFunction.JobFileName = programOptions.OutputJobFilePath;
            stepTimingFunction.StepName = jobConfiguration.Status.ToString();
            stepTimingFunction.StepID = (int)jobConfiguration.Status;
            stepTimingFunction.StartTime = DateTime.Now;
            stepTimingFunction.NumEntities = jobConfiguration.Target.Count;

            this.DisplayJobStepStartingStatus(jobConfiguration);

            FilePathMap = new FilePathMap(programOptions, jobConfiguration);

            if (this.ShouldExecute(jobConfiguration) == false)
            {
                return true;
            }

            try
            {
                List<MetricExtractMapping> entityMetricExtractMappingList = getMetricsExtractMappingList(jobConfiguration);

                // Process each target
                for (int i = 0; i < jobConfiguration.Target.Count; i++)
                {
                    Stopwatch stopWatchTarget = new Stopwatch();
                    stopWatchTarget.Start();

                    JobTarget jobTarget = jobConfiguration.Target[i];

                    StepTiming stepTimingTarget = new StepTiming();
                    stepTimingTarget.Controller = jobTarget.Controller;
                    stepTimingTarget.ApplicationName = jobTarget.Application;
                    stepTimingTarget.ApplicationID = jobTarget.ApplicationID;
                    stepTimingTarget.JobFileName = programOptions.OutputJobFilePath;
                    stepTimingTarget.StepName = jobConfiguration.Status.ToString();
                    stepTimingTarget.StepID = (int)jobConfiguration.Status;
                    stepTimingTarget.StartTime = DateTime.Now;

                    try
                    {
                        this.DisplayJobTargetStartingStatus(jobConfiguration, jobTarget, i + 1);

                        #region Target state check

                        if (jobTarget.Status != JobTargetStatus.ConfigurationValid)
                        {
                            loggerConsole.Trace("Target in invalid state {0}, skipping", jobTarget.Status);

                            continue;
                        }

                        #endregion

                        #region Target step variables

                        int numEntitiesTotal = 0;

                        #endregion

                        #region Preload all the reports that will be filtered by the subsequent entities

                        loggerConsole.Info("Completed Entity Details Data Preloading");

                        List<Event> eventsAllList = FileIOHelper.ReadListFromCSVFile<Event>(FilePathMap.EventsIndexFilePath(jobTarget), new EventReportMap());
                        List<HealthRuleViolationEvent> healthRuleViolationEventsAllList = FileIOHelper.ReadListFromCSVFile<HealthRuleViolationEvent>(FilePathMap.HealthRuleViolationsIndexFilePath(jobTarget), new HealthRuleViolationEventReportMap());
                        List<Snapshot> snapshotsAllList = FileIOHelper.ReadListFromCSVFile<Snapshot>(FilePathMap.SnapshotsIndexFilePath(jobTarget), new SnapshotReportMap());
                        List<Segment> segmentsAllList = FileIOHelper.ReadListFromCSVFile<Segment>(FilePathMap.SnapshotsSegmentsIndexFilePath(jobTarget), new SegmentReportMap());
                        List<ExitCall> exitCallsAllList = FileIOHelper.ReadListFromCSVFile<ExitCall>(FilePathMap.SnapshotsExitCallsIndexFilePath(jobTarget), new ExitCallReportMap());
                        List<ServiceEndpointCall> serviceEndpointCallsAllList = FileIOHelper.ReadListFromCSVFile<ServiceEndpointCall>(FilePathMap.SnapshotsServiceEndpointCallsIndexFilePath(jobTarget), new ServiceEndpointCallReportMap());
                        List<DetectedError> detectedErrorsAllList = FileIOHelper.ReadListFromCSVFile<DetectedError>(FilePathMap.SnapshotsDetectedErrorsIndexFilePath(jobTarget), new DetectedErrorReportMap());
                        List<BusinessData> businessDataAllList = FileIOHelper.ReadListFromCSVFile<BusinessData>(FilePathMap.SnapshotsBusinessDataIndexFilePath(jobTarget), new BusinessDataReportMap());

                        string relativePathToReportsToRemoveFromLinks = Path.Combine(
                            FilePathMap.getFileSystemSafeString(new Uri(jobTarget.Controller).Host),
                            FilePathMap.getShortenedEntityNameForFileSystem(jobTarget.Application, jobTarget.ApplicationID));

                        if (snapshotsAllList != null)
                        {
                            foreach (Snapshot snapshot in snapshotsAllList)
                            {
                                snapshot.FlameGraphLink = snapshot.FlameGraphLink.Replace(relativePathToReportsToRemoveFromLinks, "..");
                            }
                        }

                        #endregion

                        Parallel.Invoke(
                            () =>
                            {
                                #region Application

                                List<EntityApplication> applicationsList = FileIOHelper.ReadListFromCSVFile<EntityApplication>(FilePathMap.ApplicationIndexFilePath(jobTarget), new ApplicationEntityReportMap());
                                List<EntityApplication> applicationMetricsList = FileIOHelper.ReadListFromCSVFile<EntityApplication>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityApplication.ENTITY_FOLDER), new ApplicationMetricReportMap());
                                if (applicationsList != null && applicationsList.Count > 0 &&
                                    applicationMetricsList != null && applicationMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Applications ({0} entities, {1} timeranges)", applicationsList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in applicationsList)
                                    {
                                        EntityBase entityWithMetrics = applicationMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityApplication.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Applications ({0} entities)", applicationsList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, applicationsList.Count);
                                }

                                #endregion
                            },
                            () =>
                            {
                                #region Tier

                                List<EntityTier> tiersList = FileIOHelper.ReadListFromCSVFile<EntityTier>(FilePathMap.TiersIndexFilePath(jobTarget), new TierEntityReportMap());
                                List<EntityTier> tiersMetricsList = FileIOHelper.ReadListFromCSVFile<EntityTier>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityTier.ENTITY_FOLDER), new TierMetricReportMap());
                                if (tiersList != null && tiersList.Count > 0 &&
                                    tiersMetricsList != null && tiersMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Tiers ({0} entities, {1} timeranges)", tiersList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in tiersList)
                                    {
                                        EntityBase entityWithMetrics = tiersMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityTier.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Tiers ({0} entities)", tiersList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, tiersList.Count);
                                }

                                #endregion
                            },
                            () =>
                            {
                                #region Nodes

                                List<EntityNode> nodesList = FileIOHelper.ReadListFromCSVFile<EntityNode>(FilePathMap.NodesIndexFilePath(jobTarget), new NodeEntityReportMap());
                                List<EntityNode> nodesMetricsList = FileIOHelper.ReadListFromCSVFile<EntityNode>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityNode.ENTITY_FOLDER), new NodeMetricReportMap());
                                if (nodesList != null && nodesList.Count > 0 &&
                                    nodesMetricsList != null && nodesMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Nodes ({0} entities, {1} timeranges)", nodesList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in nodesList)
                                    {
                                        EntityBase entityWithMetrics = nodesMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityNode.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Nodes ({0} entities)", nodesList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, nodesList.Count);
                                }

                                #endregion
                            },
                            () =>
                            {
                                #region Backends

                                List<EntityBackend> backendsList = FileIOHelper.ReadListFromCSVFile<EntityBackend>(FilePathMap.BackendsIndexFilePath(jobTarget), new BackendEntityReportMap());
                                List<EntityBackend> backendsMetricsList = FileIOHelper.ReadListFromCSVFile<EntityBackend>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityBackend.ENTITY_FOLDER), new BackendMetricReportMap());
                                if (backendsList != null && backendsList.Count > 0 &&
                                    backendsMetricsList != null && backendsMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Backends ({0} entities, {1} timeranges)", backendsList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in backendsList)
                                    {
                                        EntityBase entityWithMetrics = backendsMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityBackend.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Backends ({0} entities)", backendsList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, backendsList.Count);
                                }

                                #endregion
                            },
                            () =>
                            {
                                #region Business Transactions

                                List<EntityBusinessTransaction> businessTransactionsList = FileIOHelper.ReadListFromCSVFile<EntityBusinessTransaction>(FilePathMap.BusinessTransactionsIndexFilePath(jobTarget), new BusinessTransactionEntityReportMap());
                                List<EntityBusinessTransaction> businessTransactionsMetricsList = FileIOHelper.ReadListFromCSVFile<EntityBusinessTransaction>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityBusinessTransaction.ENTITY_FOLDER), new BusinessTransactionMetricReportMap());
                                if (businessTransactionsList != null && businessTransactionsList.Count > 0 &&
                                    businessTransactionsMetricsList != null && businessTransactionsMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Business Transactions ({0} entities, {1} timeranges)", businessTransactionsList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in businessTransactionsList)
                                    {
                                        EntityBase entityWithMetrics = businessTransactionsMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityBusinessTransaction.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Business Transactions ({0} entities)", businessTransactionsList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, businessTransactionsList.Count);
                                }

                                #endregion
                            },
                            () =>
                            {
                                #region Service Endpoints

                                List<EntityServiceEndpoint> serviceEndpointsList = FileIOHelper.ReadListFromCSVFile<EntityServiceEndpoint>(FilePathMap.ServiceEndpointsIndexFilePath(jobTarget), new ServiceEndpointEntityReportMap());
                                List<EntityServiceEndpoint> serviceEndpointsMetricsList = FileIOHelper.ReadListFromCSVFile<EntityServiceEndpoint>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityServiceEndpoint.ENTITY_FOLDER), new ServiceEndpointMetricReportMap());
                                if (serviceEndpointsList != null && serviceEndpointsList.Count > 0 &&
                                    serviceEndpointsMetricsList != null && serviceEndpointsMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Service Endpoints ({0} entities, {1} timeranges)", serviceEndpointsList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in serviceEndpointsList)
                                    {
                                        EntityBase entityWithMetrics = serviceEndpointsMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityServiceEndpoint.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Service Endpoints ({0} entities)", serviceEndpointsList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, serviceEndpointsList.Count);
                                }

                                #endregion
                            },
                            () =>
                            {
                                #region Errors

                                List<EntityError> errorsList = FileIOHelper.ReadListFromCSVFile<EntityError>(FilePathMap.ErrorsIndexFilePath(jobTarget), new ErrorEntityReportMap());
                                List<EntityError> errorsMetricsList = FileIOHelper.ReadListFromCSVFile<EntityError>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityError.ENTITY_FOLDER), new ErrorMetricReportMap());
                                if (errorsList != null && errorsList.Count > 0 &&
                                    errorsMetricsList != null && errorsMetricsList.Count > 0)
                                {
                                    loggerConsole.Info("Report Entity Details for Errors ({0} entities, {1} timeranges)", errorsList.Count, jobConfiguration.Input.HourlyTimeRanges.Count);

                                    foreach (EntityBase entity in errorsList)
                                    {
                                        EntityBase entityWithMetrics = errorsMetricsList.Where(e => e.EntityID == entity.EntityID).FirstOrDefault();
                                        if (entityWithMetrics != null && entityWithMetrics.HasActivity == false)
                                        {
                                            logger.Trace("No metric activity in Entity Type {0} Entity {1}, skipping Entity Details output", entity.EntityType, entity.EntityName);
                                            continue;
                                        }

                                        ExcelPackage excelEntitiesDetail = createIndividualEntityDetailReportTemplate(programOptions, jobConfiguration, jobTarget);
                                        List<MetricExtractMapping> entityMetricExtractMappingListFiltered = entityMetricExtractMappingList.Where(m => m.EntityType == entity.EntityType && m.Graph == TRANSACTIONS_METRICS_SET).ToList();
                                        fillMetricValueTablesForEntityType(excelEntitiesDetail, REPORT_ENTITY_DETAILS_SHEET_METRICS, entityMetricExtractMappingListFiltered, jobTarget, EntityError.ENTITY_FOLDER);

                                        fillIndividualEntityMetricReportForEntity(
                                            programOptions,
                                            jobConfiguration,
                                            jobTarget,
                                            excelEntitiesDetail,
                                            entity,
                                            entityMetricExtractMappingListFiltered,
                                            eventsAllList,
                                            healthRuleViolationEventsAllList,
                                            snapshotsAllList,
                                            segmentsAllList,
                                            exitCallsAllList,
                                            serviceEndpointCallsAllList,
                                            detectedErrorsAllList,
                                            businessDataAllList);

                                        finalizeAndSaveIndividualEntityMetricReport(excelEntitiesDetail, entity.EntityType, FilePathMap.EntityMetricAndDetailExcelReportFilePath(entity, jobTarget, jobConfiguration.Input.TimeRange, true));
                                    }

                                    Console.WriteLine();
                                    loggerConsole.Info("Completed Entity Details for Errors ({0} entities)", errorsList.Count);

                                    Interlocked.Add(ref numEntitiesTotal, errorsList.Count);
                                }

                                #endregion
                            }
                        );

                        stepTimingTarget.NumEntities = numEntitiesTotal;
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex);
                        loggerConsole.Warn(ex);
                    }
                    finally
                    {
                        stopWatchTarget.Stop();

                        this.DisplayJobTargetEndedStatus(jobConfiguration, jobTarget, i + 1, stopWatchTarget);

                        stepTimingTarget.EndTime = DateTime.Now;
                        stepTimingTarget.Duration = stopWatchTarget.Elapsed;
                        stepTimingTarget.DurationMS = stopWatchTarget.ElapsedMilliseconds;

                        List<StepTiming> stepTimings = new List<StepTiming>(1);
                        stepTimings.Add(stepTimingTarget);
                        FileIOHelper.WriteListToCSVFile(stepTimings, new StepTimingReportMap(), FilePathMap.StepTimingReportFilePath(), true);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                loggerConsole.Error(ex);

                return false;
            }
            finally
            {
                stopWatch.Stop();

                this.DisplayJobStepEndedStatus(jobConfiguration, stopWatch);

                stepTimingFunction.EndTime = DateTime.Now;
                stepTimingFunction.Duration = stopWatch.Elapsed;
                stepTimingFunction.DurationMS = stopWatch.ElapsedMilliseconds;

                List<StepTiming> stepTimings = new List<StepTiming>(1);
                stepTimings.Add(stepTimingFunction);
                FileIOHelper.WriteListToCSVFile(stepTimings, new StepTimingReportMap(), FilePathMap.StepTimingReportFilePath(), true);
            }
        }

        public override bool ShouldExecute(JobConfiguration jobConfiguration)
        {
            logger.Trace("Input.Metrics={0}", jobConfiguration.Input.Metrics);
            loggerConsole.Trace("Input.Metrics={0}", jobConfiguration.Input.Metrics);
            logger.Trace("Input.Events={0}", jobConfiguration.Input.Events);
            loggerConsole.Trace("Input.Events={0}", jobConfiguration.Input.Events);
            logger.Trace("Input.Flowmaps={0}", jobConfiguration.Input.Flowmaps);
            loggerConsole.Trace("Input.Flowmaps={0}", jobConfiguration.Input.Flowmaps);
            logger.Trace("Input.Snapshots={0}", jobConfiguration.Input.Snapshots);
            loggerConsole.Trace("Input.Snapshots={0}", jobConfiguration.Input.Snapshots);
            logger.Trace("Output.EntityDetails={0}", jobConfiguration.Output.EntityDetails);
            loggerConsole.Trace("Output.EntityDetails={0}", jobConfiguration.Output.EntityDetails);
            if ((jobConfiguration.Input.Metrics == false && jobConfiguration.Input.Events == false && jobConfiguration.Input.Flowmaps == false && jobConfiguration.Input.Snapshots == false) || jobConfiguration.Output.EntityDetails == false)
            {
                loggerConsole.Trace("Skipping report of per-entity details");
            }
            return ((jobConfiguration.Input.Metrics == true || jobConfiguration.Input.Events == true || jobConfiguration.Input.Flowmaps == true || jobConfiguration.Input.Snapshots == true) && jobConfiguration.Output.FlameGraphs == true);
        }

        private ExcelPackage createIndividualEntityDetailReportTemplate(ProgramOptions programOptions, JobConfiguration jobConfiguration, JobTarget jobTarget)
        {
            #region Prepare the report package

            // Prepare package
            ExcelPackage excelEntityDetail = new ExcelPackage();
            excelEntityDetail.Workbook.Properties.Author = String.Format("AppDynamics DEXTER {0}", Assembly.GetEntryAssembly().GetName().Version);
            excelEntityDetail.Workbook.Properties.Title = "AppDynamics DEXTER Entity Detail Report";
            excelEntityDetail.Workbook.Properties.Subject = programOptions.JobName;

            excelEntityDetail.Workbook.Properties.Comments = String.Format("Targets={0}\nFrom={1:o}\nTo={2:o}", jobConfiguration.Target.Count, jobConfiguration.Input.TimeRange.From, jobConfiguration.Input.TimeRange.To);

            #endregion

            #region Parameters sheet

            // Parameters sheet
            ExcelWorksheet sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_SHEET_PARAMETERS);

            var hyperLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("HyperLinkStyle");
            hyperLinkStyle.Style.Font.UnderLineType = ExcelUnderLineType.Single;
            hyperLinkStyle.Style.Font.Color.SetColor(colorBlueForHyperlinks);

            fillReportParametersSheet(sheet, jobConfiguration, "AppDynamics DEXTER Entity Detail Report");

            #endregion

            #region TOC sheet

            // Navigation sheet with link to other sheets
            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_SHEET_TOC);

            #endregion

            #region Controller sheet

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_CONTROLLERS);
            sheet.Cells[1, 1].Value = "Table of Contents";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, 1);

            ExcelRangeBase range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(FilePathMap.ControllerIndexFilePath(jobTarget), 0, sheet, REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT, 1);
            if (range != null)
            {
                ExcelTable table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_CONTROLLERS);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.Medium2;
                table.ShowFilter = true;
                table.ShowTotal = false;

                sheet.Column(table.Columns["Controller"].Position + 1).Width = 25;
                sheet.Column(table.Columns["UserName"].Position + 1).Width = 25;
            }

            #endregion

            #region Other sheets

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_SUMMARY);
            sheet.Cells[1, 1].Value = "TOC";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, 1);

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_ACTIVITYGRID);
            sheet.Cells[1, 1].Value = "Table of Contents";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, 1);

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_TIMELINE);
            sheet.Cells[1, 1].Value = "Table of Contents";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_GRAPHS_SHEET_START_TABLE_AT + 1, 6);

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_EVENTS);
            sheet.Cells[1, 1].Value = "Table of Contents";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, 1);

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS);
            sheet.Cells[1, 1].Value = "Table of Contents";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, 1);

            sheet = excelEntityDetail.Workbook.Worksheets.Add(REPORT_ENTITY_DETAILS_SHEET_METRICS);
            sheet.Cells[1, 1].Value = "Table of Contents";
            sheet.Cells[1, 2].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", REPORT_SHEET_TOC);
            sheet.Cells[1, 2].StyleName = "HyperLinkStyle";
            sheet.View.FreezePanes(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, 1);

            #endregion

            return excelEntityDetail;
        }

        private void fillIndividualEntityMetricReportForEntity(
            ProgramOptions programOptions,
            JobConfiguration jobConfiguration,
            JobTarget jobTarget,
            ExcelPackage excelEntityDetail,
            EntityBase entity,
            List<MetricExtractMapping> entityMetricExtractMappingList,
            List<Event> eventsAllList,
            List<HealthRuleViolationEvent> healthRuleViolationEventsAllList,
            List<Snapshot> snapshotsAllList,
            List<Segment> segmentsAllList,
            List<ExitCall> exitCallsAllList,
            List<ServiceEndpointCall> serviceEndpointCallsAllList,
            List<DetectedError> detectedErrorsAllList,
            List<BusinessData> businessDataAllList)
        {
            #region Target step variables

            string activityGridFilePath = String.Empty;

            int fromRow = 1;

            // Report tables and ranges
            ExcelRangeBase range;
            ExcelTable table;

            ExcelTable tableEvents = null;
            ExcelTable tableSnapshots = null;

            #endregion

            #region Parameter sheet

            ExcelWorksheet sheet = excelEntityDetail.Workbook.Worksheets[REPORT_SHEET_PARAMETERS];

            int l = sheet.Dimension.Rows + 2;
            sheet.Cells[l, 1].Value = "Type";
            sheet.Cells[l, 2].Value = entity.EntityType;
            l++;
            sheet.Cells[l, 1].Value = "Name";
            sheet.Cells[l, 2].Value = entity.EntityName;

            #endregion

            #region Entity Metrics Summary

            #region Filter things by type of entity 

            MemoryStream memoryStreamEntitiesFullRange = null;
            MemoryStream memoryStreamEntitiesHourlyRanges = null;

            try
            {
                if (entity.EntityType == EntityApplication.ENTITY_TYPE)
                {
                    List<EntityApplication> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityApplication>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityApplication.ENTITY_FOLDER), new ApplicationMetricReportMap());
                    List<EntityApplication> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityApplication>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityApplication.ENTITY_FOLDER), new ApplicationMetricReportMap());

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new ApplicationMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new ApplicationMetricReportMap());
                }
                else if (entity.EntityType == EntityTier.ENTITY_TYPE)
                {
                    List<EntityTier> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityTier>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityTier.ENTITY_FOLDER), new TierMetricReportMap()).Where(e => e.TierID == entity.EntityID).ToList();
                    List<EntityTier> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityTier>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityTier.ENTITY_FOLDER), new TierMetricReportMap()).Where(e => e.TierID == entity.EntityID).ToList();

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new TierMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new TierMetricReportMap());
                }
                else if (entity.EntityType == EntityNode.ENTITY_TYPE)
                {
                    List<EntityNode> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityNode>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityNode.ENTITY_FOLDER), new NodeMetricReportMap()).Where(e => e.NodeID == entity.EntityID).ToList();
                    List<EntityNode> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityNode>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityNode.ENTITY_FOLDER), new NodeMetricReportMap()).Where(e => e.NodeID == entity.EntityID).ToList();

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new NodeMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new NodeMetricReportMap());
                }
                else if (entity.EntityType == EntityBackend.ENTITY_TYPE)
                {
                    List<EntityBackend> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityBackend>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityBackend.ENTITY_FOLDER), new BackendMetricReportMap()).Where(e => e.BackendID == entity.EntityID).ToList();
                    List<EntityBackend> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityBackend>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityBackend.ENTITY_FOLDER), new BackendMetricReportMap()).Where(e => e.BackendID == entity.EntityID).ToList();

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new BackendMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new BackendMetricReportMap());
                }
                else if (entity.EntityType == EntityBusinessTransaction.ENTITY_TYPE)
                {
                    List<EntityBusinessTransaction> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityBusinessTransaction>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityBusinessTransaction.ENTITY_FOLDER), new BusinessTransactionMetricReportMap()).Where(e => e.BTID == entity.EntityID).ToList();
                    List<EntityBusinessTransaction> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityBusinessTransaction>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityBusinessTransaction.ENTITY_FOLDER), new BusinessTransactionMetricReportMap()).Where(e => e.BTID == entity.EntityID).ToList();

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new BusinessTransactionMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new BusinessTransactionMetricReportMap());
                }
                else if (entity.EntityType == EntityServiceEndpoint.ENTITY_TYPE)
                {
                    List<EntityServiceEndpoint> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityServiceEndpoint>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityServiceEndpoint.ENTITY_FOLDER), new ServiceEndpointMetricReportMap()).Where(e => e.SEPID == entity.EntityID).ToList();
                    List<EntityServiceEndpoint> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityServiceEndpoint>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityServiceEndpoint.ENTITY_FOLDER), new ServiceEndpointMetricReportMap()).Where(e => e.SEPID == entity.EntityID).ToList();

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new ServiceEndpointMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new ServiceEndpointMetricReportMap());
                }
                else if (entity.EntityType == EntityError.ENTITY_TYPE)
                {
                    List<EntityError> entitiesFullRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityError>(FilePathMap.EntitiesFullIndexFilePath(jobTarget, EntityError.ENTITY_FOLDER), new ErrorMetricReportMap()).Where(e => e.ErrorID == entity.EntityID).ToList();
                    List<EntityError> entitiesHourRangeFiltered = FileIOHelper.ReadListFromCSVFile<EntityError>(FilePathMap.EntitiesHourIndexFilePath(jobTarget, EntityError.ENTITY_FOLDER), new ErrorMetricReportMap()).Where(e => e.ErrorID == entity.EntityID).ToList();

                    memoryStreamEntitiesFullRange = FileIOHelper.WriteListToMemoryStream(entitiesFullRangeFiltered, new ErrorMetricReportMap());
                    memoryStreamEntitiesHourlyRanges = FileIOHelper.WriteListToMemoryStream(entitiesHourRangeFiltered, new ErrorMetricReportMap());
                }
            }
            catch (ArgumentNullException ex)
            {
                // The file is missing 
            }


            #endregion

            #region Output Calls and Response sheet Full range

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_SUMMARY];

            string relativePathToReportsToRemoveFromLinks = Path.Combine(
                FilePathMap.getFileSystemSafeString(new Uri(jobTarget.Controller).Host),
                FilePathMap.getShortenedEntityNameForFileSystem(jobTarget.Application, jobTarget.ApplicationID));

            // Full range table
            fromRow = REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT;
            if (memoryStreamEntitiesFullRange != null)
            {
                range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(memoryStreamEntitiesFullRange, 0, sheet, fromRow, 1);
                memoryStreamEntitiesFullRange.Close();
                memoryStreamEntitiesFullRange.Dispose();

                if (range != null)
                {
                    table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_ENTITY_FULL);

                    // Now loop through all lines and strip out references from Flame link
                    if (table.Columns["FlameGraphLink"] != null)
                    {
                        for (int i = range.Start.Row + 1; i <= range.End.Row; i++)
                        {
                            sheet.Cells[i, table.Columns["FlameGraphLink"].Position + 1].Formula = sheet.Cells[i, table.Columns["FlameGraphLink"].Position + 1].Formula.Replace(relativePathToReportsToRemoveFromLinks, "..");
                        }
                    }
                    if (table.Columns["MetricGraphLink"] != null)
                    {
                        for (int i = range.Start.Row + 1; i <= range.End.Row; i++)
                        {
                            sheet.Cells[i, table.Columns["MetricGraphLink"].Position + 1].Formula = sheet.Cells[i, table.Columns["MetricGraphLink"].Position + 1].Formula.Replace(relativePathToReportsToRemoveFromLinks, "..");
                        }
                    }

                    fromRow = fromRow + range.Rows + 2;
                }
            }

            #endregion

            #region Output Calls and Response sheet Hourly ranges

            // Hourly table
            sheet.Cells[fromRow - 1, 1].Value = "Hourly";

            if (memoryStreamEntitiesHourlyRanges != null)
            {
                range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(memoryStreamEntitiesHourlyRanges, 0, sheet, fromRow, 1);
                memoryStreamEntitiesHourlyRanges.Close();
                memoryStreamEntitiesHourlyRanges.Dispose();

                if (range != null)
                {
                    table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_ENTITY_HOURLY);

                    // Now loop through all lines and strip out references from Flame link
                    if (table.Columns["FlameGraphLink"] != null)
                    {
                        for (int i = range.Start.Row + 1; i <= range.End.Row; i++)
                        {
                            sheet.Cells[i, table.Columns["FlameGraphLink"].Position + 1].Formula = sheet.Cells[i, table.Columns["FlameGraphLink"].Position + 1].Formula.Replace(relativePathToReportsToRemoveFromLinks, "..");
                        }
                    }
                    if (table.Columns["MetricGraphLink"] != null)
                    {
                        for (int i = range.Start.Row + 1; i <= range.End.Row; i++)
                        {
                            sheet.Cells[i, table.Columns["MetricGraphLink"].Position + 1].Formula = sheet.Cells[i, table.Columns["MetricGraphLink"].Position + 1].Formula.Replace(relativePathToReportsToRemoveFromLinks, "..");
                        }
                    }
                }
            }

            #endregion

            #endregion

            #region Activity grid sheet

            #region Filter things by type of entity

            MemoryStream memoryStreamActivityFlow = null;
            try
            {
                if (entity.EntityType == EntityApplication.ENTITY_TYPE)
                {
                    List<ActivityFlow> activityFlowFiltered = FileIOHelper.ReadListFromCSVFile<ActivityFlow>(FilePathMap.ApplicationFlowmapIndexFilePath(jobTarget), new ApplicationActivityFlowReportMap());

                    memoryStreamActivityFlow = FileIOHelper.WriteListToMemoryStream(activityFlowFiltered, new ApplicationActivityFlowReportMap());

                    //EPPlusCSVHelper.ReadCSVFileIntoExcelRange(FilePathMap.ApplicationFlowmapIndexFilePath(jobTarget), 0, sheet, fromRow, 1);
                }
                else if (entity.EntityType == EntityTier.ENTITY_TYPE)
                {
                    List<ActivityFlow> activityFlowFiltered = FileIOHelper.ReadListFromCSVFile<ActivityFlow>(FilePathMap.TiersFlowmapIndexFilePath(jobTarget), new TierActivityFlowReportMap()).Where(e => e.TierID == entity.EntityID).ToList();

                    memoryStreamActivityFlow = FileIOHelper.WriteListToMemoryStream(activityFlowFiltered, new TierActivityFlowReportMap());
                }
                else if (entity.EntityType == EntityNode.ENTITY_TYPE)
                {
                    List<ActivityFlow> activityFlowFiltered = FileIOHelper.ReadListFromCSVFile<ActivityFlow>(FilePathMap.NodesFlowmapIndexFilePath(jobTarget), new NodeActivityFlowReportMap()).Where(e => e.NodeID == entity.EntityID).ToList();

                    memoryStreamActivityFlow = FileIOHelper.WriteListToMemoryStream(activityFlowFiltered, new NodeActivityFlowReportMap());
                }
                else if (entity.EntityType == EntityBackend.ENTITY_TYPE)
                {
                    List<ActivityFlow> activityFlowFiltered = FileIOHelper.ReadListFromCSVFile<ActivityFlow>(FilePathMap.BackendsFlowmapIndexFilePath(jobTarget), new BackendActivityFlowReportMap()).Where(e => e.BackendID == entity.EntityID).ToList();

                    memoryStreamActivityFlow = FileIOHelper.WriteListToMemoryStream(activityFlowFiltered, new BackendActivityFlowReportMap());
                }
                else if (entity.EntityType == EntityBusinessTransaction.ENTITY_TYPE)
                {
                    List<ActivityFlow> activityFlowFiltered = FileIOHelper.ReadListFromCSVFile<ActivityFlow>(FilePathMap.BusinessTransactionsFlowmapIndexFilePath(jobTarget), new BusinessTransactionActivityFlowReportMap()).Where(e => e.BTID == entity.EntityID).ToList();

                    memoryStreamActivityFlow = FileIOHelper.WriteListToMemoryStream(activityFlowFiltered, new BusinessTransactionActivityFlowReportMap());
                }
            }
            catch (ArgumentNullException ex)
            {
                // The file is missing 
            }

            #endregion

            #region Output Activity grid sheet

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_ACTIVITYGRID];

            fromRow = REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT;
            if (memoryStreamActivityFlow != null)
            {
                range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(memoryStreamActivityFlow, 0, sheet, fromRow, 1);

                memoryStreamActivityFlow.Close();
                memoryStreamActivityFlow.Dispose();
            }

            #endregion

            #endregion

            #region Events sheet

            #region Filter events by type of entity 

            // Filter events if necessary
            List<Event> eventsFilteredList = null;

            switch (entity.EntityType)
            {
                case EntityApplication.ENTITY_TYPE:
                    // The Application report has all events
                    eventsFilteredList = eventsAllList;
                    break;

                case EntityTier.ENTITY_TYPE:
                    // Filter events for the Tier
                    if (eventsAllList != null)
                    {
                        eventsFilteredList = eventsAllList.Where(e => e.TierID == ((EntityTier)entity).TierID).ToList();
                    }
                    break;

                case EntityNode.ENTITY_TYPE:
                    // Filter events for the Node
                    if (eventsAllList != null)
                    {
                        eventsFilteredList = eventsAllList.Where(e => e.NodeID == ((EntityNode)entity).NodeID).ToList();
                    }
                    break;

                case EntityBusinessTransaction.ENTITY_TYPE:
                    // Filter events for the Business Transaction
                    if (eventsAllList != null)
                    {
                        eventsFilteredList = eventsAllList.Where(e => e.BTID == ((EntityBusinessTransaction)entity).BTID).ToList();
                    }
                    break;

                default:
                    // Display nothing
                    break;
            }

            #endregion

            #region Output Events sheet

            // Output events
            fromRow = REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT;
            if (eventsFilteredList != null && eventsFilteredList.Count > 0)
            {
                sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_EVENTS];

                using (MemoryStream ms = FileIOHelper.WriteListToMemoryStream(eventsFilteredList, new EventReportMap()))
                {
                    range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(ms, 0, sheet, fromRow, 1);
                    if (range != null && range.Rows > 1)
                    {
                        tableEvents = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_EVENTS);
                    }
                }
            }

            #endregion

            #endregion

            #region Snapshots, Segments, Exit Calls, Service Endpoint Calls, Business Data

            #region Filter events by type of entity

            List<Snapshot> snapshotsFilteredList = new List<Snapshot>();
            List<Segment> segmentsFilteredList = new List<Segment>();

            switch (entity.EntityType)
            {
                case EntityApplication.ENTITY_TYPE:
                    // The Application report has all snapshots, segments, call exits etc.
                    snapshotsFilteredList = snapshotsAllList;

                    break;

                case EntityTier.ENTITY_TYPE:
                    // Filter snapshots starting at this Tier
                    List<Snapshot> snapshotsStartingAtThisEntity = null;
                    if (snapshotsAllList != null)
                    {
                        snapshotsStartingAtThisEntity = snapshotsAllList.Where(s => s.TierID == ((EntityTier)entity).TierID).ToList();
                    }

                    // Filter snapshots that start elsewhere, but include this tier
                    List<Snapshot> snapshotsCrossingThisEntity = new List<Snapshot>();
                    segmentsFilteredList = new List<Segment>();
                    if (segmentsAllList != null && snapshotsAllList != null)
                    {
                        var uniqueSnapshotIDs = segmentsAllList.Where(s => s.TierID == ((EntityTier)entity).TierID).ToList().Select(e => e.RequestID).Distinct();
                        foreach (string requestID in uniqueSnapshotIDs)
                        {
                            Snapshot snapshotForThisRequest = snapshotsAllList.Find(s => s.RequestID == requestID);
                            if (snapshotForThisRequest != null)
                            {
                                snapshotsCrossingThisEntity.Add(snapshotForThisRequest);
                            }

                            List<Segment> segmentRowsForThisRequest = segmentsAllList.Where(s => s.RequestID == requestID).ToList();
                            segmentsFilteredList.AddRange(segmentRowsForThisRequest);
                        }
                    }

                    // Combine both and make them unique
                    snapshotsFilteredList = new List<Snapshot>();
                    if (snapshotsStartingAtThisEntity != null) { snapshotsFilteredList.AddRange(snapshotsStartingAtThisEntity); }
                    if (snapshotsCrossingThisEntity != null) { snapshotsFilteredList.AddRange(snapshotsCrossingThisEntity); }
                    snapshotsFilteredList = snapshotsFilteredList.Distinct().ToList();

                    break;

                case EntityNode.ENTITY_TYPE:
                    // Filter snapshots starting at this Tier and Node
                    snapshotsStartingAtThisEntity = null;
                    if (snapshotsAllList != null)
                    {
                        snapshotsStartingAtThisEntity = snapshotsAllList.Where(s => s.TierID == ((EntityNode)entity).TierID && s.NodeID == ((EntityNode)entity).NodeID).ToList();
                    }

                    // Filter snapshots starting elsewhere, but including this Tier and Node
                    snapshotsCrossingThisEntity = new List<Snapshot>();
                    segmentsFilteredList = new List<Segment>();
                    if (segmentsAllList != null && snapshotsAllList != null)
                    {
                        var uniqueSnapshotIDs = segmentsAllList.Where(s => s.TierID == ((EntityNode)entity).TierID && s.NodeID == ((EntityNode)entity).NodeID).ToList().Select(e => e.RequestID).Distinct();
                        foreach (string requestID in uniqueSnapshotIDs)
                        {
                            Snapshot snapshotForThisRequest = snapshotsAllList.Find(s => s.RequestID == requestID);
                            if (snapshotForThisRequest != null)
                            {
                                snapshotsCrossingThisEntity.Add(snapshotForThisRequest);
                            }

                            List<Segment> segmentsForThisRequestList = segmentsAllList.Where(s => s.RequestID == requestID).ToList();
                            segmentsFilteredList.AddRange(segmentsForThisRequestList);
                        }
                    }

                    // Combine both and make them unique
                    snapshotsFilteredList = new List<Snapshot>();
                    if (snapshotsStartingAtThisEntity != null) { snapshotsFilteredList.AddRange(snapshotsStartingAtThisEntity); }
                    if (snapshotsCrossingThisEntity != null) { snapshotsFilteredList.AddRange(snapshotsCrossingThisEntity); }
                    snapshotsFilteredList = snapshotsFilteredList.Distinct().ToList();

                    break;

                case EntityBackend.ENTITY_TYPE:
                    // Filter snapshots calling this Backend
                    if (exitCallsAllList != null)
                    {
                        var uniqueSnapshotIDs = exitCallsAllList.Where(e => e.ToEntityID == ((EntityBackend)entity).BackendID).ToList().Select(e => e.RequestID).Distinct();
                        foreach (string requestID in uniqueSnapshotIDs)
                        {
                            if (snapshotsAllList != null)
                            {
                                snapshotsFilteredList.AddRange(snapshotsAllList.Where(s => s.RequestID == requestID).ToList());
                            }
                        }
                    }

                    break;

                case EntityBusinessTransaction.ENTITY_TYPE:
                    // Filter everything by BTs 
                    if (snapshotsAllList != null)
                    {
                        snapshotsFilteredList = snapshotsAllList.Where(s => s.BTID == ((EntityBusinessTransaction)entity).BTID).ToList();
                    }

                    break;

                case EntityServiceEndpoint.ENTITY_TYPE:
                    // Filter snapshots that call this SEP
                    if (serviceEndpointCallsAllList != null)
                    {
                        var uniqueSnapshotIDs = serviceEndpointCallsAllList.Where(s => s.SEPID == ((EntityServiceEndpoint)entity).SEPID).ToList().Select(e => e.RequestID).Distinct();
                        foreach (string requestID in uniqueSnapshotIDs)
                        {
                            if (snapshotsAllList != null)
                            {
                                snapshotsFilteredList.AddRange(snapshotsAllList.Where(s => s.RequestID == requestID).ToList());
                            }
                        }
                    }

                    break;

                case EntityError.ENTITY_TYPE:
                    // Filter snapshots that had this error
                    if (detectedErrorsAllList != null)
                    {
                        var uniqueSnapshotIDs = detectedErrorsAllList.Where(e => e.ErrorID == ((EntityError)entity).ErrorID).ToList().Select(e => e.RequestID).Distinct();
                        foreach (string requestID in uniqueSnapshotIDs)
                        {
                            if (snapshotsAllList != null)
                            {
                                snapshotsFilteredList.AddRange(snapshotsAllList.Where(s => s.RequestID == requestID).ToList());
                            }
                        }
                    }

                    break;

                default:
                    // Will never hit here, because all the values are already taken care of
                    // But do nothing anyway
                    break;
            }

            #endregion

            #region Snapshots sheet

            fromRow = REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT;
            if (snapshotsFilteredList != null && snapshotsFilteredList.Count > 0)
            {
                sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS];

                using (MemoryStream ms = FileIOHelper.WriteListToMemoryStream(snapshotsFilteredList, new SnapshotReportMap()))
                {
                    range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(ms, 0, sheet, fromRow, 1);
                    if (range != null && range.Rows > 1)
                    {
                        tableSnapshots = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_SNAPSHOTS);
                    }
                }
            }

            #endregion

            #endregion

            #region Detail sheet with Graphs, Snapshots and Events

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_TIMELINE];

            ExcelWorksheet sheetMetrics = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_METRICS];

            #region Legend and other pretties

            // Names of entities
            sheet.Cells[2, 1].Value = "Type";
            sheet.Cells[2, 2].Value = entity.EntityType;
            sheet.Cells[3, 1].Value = "Name";
            sheet.Cells[3, 2].Value = entity.EntityName;

            // Legend
            sheet.Cells[5, 1].Value = LEGEND_THICK_LINE;
            sheet.Cells[5, 1].Style.Font.Color.SetColor(colorMetricART);
            sheet.Cells[5, 2].Value = METRIC_ART_FULLNAME;
            sheet.Cells[6, 1].Value = LEGEND_THICK_LINE;
            sheet.Cells[6, 1].Style.Font.Color.SetColor(colorMetricCPM);
            sheet.Cells[6, 2].Value = METRIC_CPM_FULLNAME;
            sheet.Cells[7, 1].Value = LEGEND_THICK_LINE;
            sheet.Cells[7, 1].Style.Font.Color.SetColor(colorMetricEPM);
            sheet.Cells[7, 2].Value = METRIC_EPM_FULLNAME;
            sheet.Cells[8, 1].Value = LEGEND_THICK_LINE;
            sheet.Cells[8, 1].Style.Font.Color.SetColor(colorMetricEXCPM);
            sheet.Cells[8, 2].Value = METRIC_EXCPM_FULLNAME;
            sheet.Cells[9, 1].Value = LEGEND_THICK_LINE;
            sheet.Cells[9, 1].Style.Font.Color.SetColor(colorMetricHTTPEPM);
            sheet.Cells[9, 2].Value = METRIC_HTTPEPM_FULLNAME;

            #endregion

            #region Prepare Styles 

            var minuteHeadingtyle = sheet.Workbook.Styles.CreateNamedStyle("MinuteHeadingStyle");
            minuteHeadingtyle.Style.Font.Size = 9;

            var eventHeadingStyle = sheet.Workbook.Styles.CreateNamedStyle("EventHeadingStyle");
            eventHeadingStyle.Style.Font.Size = 9;

            var infoEventLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("InfoEventLinkStyle");
            infoEventLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            infoEventLinkStyle.Style.Fill.BackgroundColor.SetColor(colorLightBlueForInfoEvents); // This is sort of Color.LightBlue, but I like it better
            infoEventLinkStyle.Style.Font.Color.SetColor(Color.White);
            infoEventLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var warnEventLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("WarnEventLinkStyle");
            warnEventLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            warnEventLinkStyle.Style.Fill.BackgroundColor.SetColor(colorOrangeForWarnEvents);
            warnEventLinkStyle.Style.Font.Color.SetColor(Color.Black);
            warnEventLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var errorEventLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("ErrorEventLinkStyle");
            errorEventLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            errorEventLinkStyle.Style.Fill.BackgroundColor.SetColor(colorRedForErrorEvents);
            errorEventLinkStyle.Style.Font.Color.SetColor(Color.Black);
            errorEventLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var normalSnapshotLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("NormalSnapshotLinkStyle");
            normalSnapshotLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            normalSnapshotLinkStyle.Style.Fill.BackgroundColor.SetColor(colorGreenForNormalSnapshots);
            normalSnapshotLinkStyle.Style.Font.Color.SetColor(Color.White);
            normalSnapshotLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var slowSnapshotLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("SlowSnapshotLinkStyle");
            slowSnapshotLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            slowSnapshotLinkStyle.Style.Fill.BackgroundColor.SetColor(colorYellowForSlowSnapshots);
            slowSnapshotLinkStyle.Style.Font.Color.SetColor(Color.Black);
            slowSnapshotLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var verySlowSnapshotLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("VerySlowSnapshotLinkStyle");
            verySlowSnapshotLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            verySlowSnapshotLinkStyle.Style.Fill.BackgroundColor.SetColor(colorOrangeForVerySlowSnapshots);
            verySlowSnapshotLinkStyle.Style.Font.Color.SetColor(Color.Black);
            verySlowSnapshotLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var stallSnapshotLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("StallSnapshotLinkStyle");
            stallSnapshotLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            stallSnapshotLinkStyle.Style.Fill.BackgroundColor.SetColor(colorOrangeForStallSnapshots);
            stallSnapshotLinkStyle.Style.Font.Color.SetColor(Color.White);
            stallSnapshotLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            var errorSnapshotLinkStyle = sheet.Workbook.Styles.CreateNamedStyle("ErrorSnapshotLinkStyle");
            errorSnapshotLinkStyle.Style.Fill.PatternType = ExcelFillStyle.Solid;
            errorSnapshotLinkStyle.Style.Fill.BackgroundColor.SetColor(colorRedForErrorSnapshots);
            errorSnapshotLinkStyle.Style.Font.Color.SetColor(Color.Black);
            errorSnapshotLinkStyle.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            #endregion

            #region Output headers for each of the hour ranges 

            ExcelWorksheet sheetDetails = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_SUMMARY];
            ExcelTable tableDetails = sheetDetails.Tables[REPORT_ENTITY_DETAILS_TABLE_ENTITY_HOURLY];

            // Prepare vertical section for each of the hours
            int columnOffsetBegin = 6;
            int columnOffsetBetweenRanges = 1;
            int numCellsPerHourRange = 60;
            for (int i = 0; i < jobConfiguration.Input.HourlyTimeRanges.Count; i++)
            {
                JobTimeRange jobTimeRange = jobConfiguration.Input.HourlyTimeRanges[i];

                // Adjust columns in sheet
                int columnIndexTimeRangeStart = columnOffsetBegin + i * columnOffsetBetweenRanges + i * numCellsPerHourRange;
                int columnIndexTimeRangeEnd = columnIndexTimeRangeStart + numCellsPerHourRange;
                int minuteNumber = 0;
                for (int columnIndex = columnIndexTimeRangeStart; columnIndex < columnIndexTimeRangeEnd; columnIndex++)
                {
                    sheet.Column(columnIndex).Width = 2.5;
                    sheet.Cells[REPORT_ENTITY_DETAILS_GRAPHS_SHEET_START_TABLE_AT, columnIndex].Value = minuteNumber;
                    sheet.Cells[REPORT_ENTITY_DETAILS_GRAPHS_SHEET_START_TABLE_AT, columnIndex].StyleName = "MinuteHeadingStyle";

                    sheet.Column(columnIndex).OutlineLevel = 1;
                    minuteNumber++;
                }

                // Add summaries from the hourly metric breakdowns
                if (tableDetails != null)
                {
                    if (entity.EntityType != EntityError.ENTITY_TYPE)
                    {
                        sheet.Cells[1, columnIndexTimeRangeStart + 0].Value = "Calls";
                        sheet.Cells[2, columnIndexTimeRangeStart + 0].Value = sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["Calls"].Position + 1].Value.ToString();
                        sheet.Cells[1, columnIndexTimeRangeStart + 6].Value = "CPM";
                        sheet.Cells[2, columnIndexTimeRangeStart + 6].Value = sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["CPM"].Position + 1].Value.ToString();
                        sheet.Cells[1, columnIndexTimeRangeStart + 12].Value = "ART";
                        sheet.Cells[2, columnIndexTimeRangeStart + 12].Value = sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["ART"].Position + 1].Value.ToString();
                        sheet.Cells[1, columnIndexTimeRangeStart + 18].Value = "Errors";
                        sheet.Cells[2, columnIndexTimeRangeStart + 18].Value = sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["Errors"].Position + 1].Value.ToString();
                        sheet.Cells[1, columnIndexTimeRangeStart + 24].Value = "EPM";
                        sheet.Cells[2, columnIndexTimeRangeStart + 24].Value = sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["EPM"].Position + 1].Value.ToString();
                        sheet.Cells[1, columnIndexTimeRangeStart + 30].Value = "Errors %";
                        sheet.Cells[2, columnIndexTimeRangeStart + 30].Value = String.Format("{0}%", sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["ErrorsPercentage"].Position + 1].Value.ToString());
                    }
                    else
                    {
                        sheet.Cells[1, columnIndexTimeRangeStart + 24].Value = "EPM";
                        sheet.Cells[2, columnIndexTimeRangeStart + 24].Value = sheetDetails.Cells[tableDetails.Address.Start.Row + 1 + i, tableDetails.Columns["EPM"].Position + 1].Value.ToString();
                    }
                }

                sheet.Cells[4, columnIndexTimeRangeStart + 0].Value = "Calls";
                sheet.Cells[4, columnIndexTimeRangeStart + 0].StyleName = "MinuteHeadingStyle";
                sheet.Cells[4, columnIndexTimeRangeStart + 56].Value = "Response";
                sheet.Cells[4, columnIndexTimeRangeStart + 56].StyleName = "MinuteHeadingStyle";
                sheet.Cells[1, columnIndexTimeRangeStart + 40].Value = "From";
                sheet.Cells[1, columnIndexTimeRangeStart + 50].Value = "To";
                sheet.Cells[2, columnIndexTimeRangeStart + 37].Value = "Local";
                sheet.Cells[2, columnIndexTimeRangeStart + 40].Value = jobTimeRange.From.ToLocalTime().ToString("G");
                sheet.Cells[2, columnIndexTimeRangeStart + 50].Value = jobTimeRange.To.ToLocalTime().ToString("G");
                sheet.Cells[3, columnIndexTimeRangeStart + 37].Value = "UTC";
                sheet.Cells[3, columnIndexTimeRangeStart + 40].Value = jobTimeRange.From.ToString("G");
                sheet.Cells[3, columnIndexTimeRangeStart + 50].Value = jobTimeRange.To.ToString("G");
            }

            #endregion

            #region Output Metric Graphs

            // Load metric index locations file for later use
            List<EntityHourlyMetricValueLocation> entityMetricValuesLocations = FileIOHelper.ReadListFromCSVFile<EntityHourlyMetricValueLocation>(FilePathMap.MetricsLocationIndexFilePath(jobTarget, entity.FolderName), new EntityHourlyMetricValueLocationReportMap());

            // Output graphs for every hour range one at a time
            for (int indexOfTimeRange = 0; indexOfTimeRange < jobConfiguration.Input.HourlyTimeRanges.Count; indexOfTimeRange++)
            {
                JobTimeRange jobTimeRange = jobConfiguration.Input.HourlyTimeRanges[indexOfTimeRange];

                int columnIndexOfCurrentRangeBegin = columnOffsetBegin + indexOfTimeRange * columnOffsetBetweenRanges + indexOfTimeRange * numCellsPerHourRange;

                ExcelChart chartPrimaryAxis = null;
                ExcelChart chartSecondaryAxis = null;

                // Output metrics into numbers row and graphs one at a time
                for (int indexOfMetricMapping = 0; indexOfMetricMapping < entityMetricExtractMappingList.Count; indexOfMetricMapping++)
                {
                    MetricExtractMapping metricExtractMapping = entityMetricExtractMappingList[indexOfMetricMapping];

                    // Get indexed table location mapping for this specific metric/entity/timerange combo
                    List<EntityHourlyMetricValueLocation> entityMetricValuesLocationsForThisMetric = entityMetricValuesLocations.Where(m =>
                        m.MetricName == metricExtractMapping.MetricName &&
                        m.EntityID == entity.EntityID &&
                        m.FromUtc >= jobTimeRange.From &&
                        m.ToUtc < jobTimeRange.To).ToList();
                    if (entityMetricValuesLocationsForThisMetric != null && entityMetricValuesLocationsForThisMetric.Count > 0)
                    {
                        // Output the numeric values
                        ExcelTable tableMetrics = sheetMetrics.Tables[String.Format(REPORT_ENTITY_DETAILS_METRIC_TABLE_METRIC_VALUES, entity.FolderName, metricExtractMapping.FolderName)];
                        if (tableMetrics != null)
                        {
                            // Should be only metric index for this value 1 in here
                            EntityHourlyMetricValueLocation entityMetricValuesLocationForThisMetric = entityMetricValuesLocationsForThisMetric[0];

                            // Get reference to or create graphs if they don't exist
                            // Always create primary chart
                            ExcelChart chart = null;
                            if (chartPrimaryAxis == null)
                            {
                                chartPrimaryAxis = sheet.Drawings.AddChart(String.Format(REPORT_ENTITY_DETAILS_METRIC_GRAPH, entity.EntityType, jobTimeRange.From), eChartType.XYScatterLinesNoMarkers);
                                chartPrimaryAxis.SetPosition(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT, 0, columnIndexOfCurrentRangeBegin - 1, 0);
                                chartPrimaryAxis.SetSize(1020, 200);
                                chartPrimaryAxis.Style = eChartStyle.Style17;
                                DateTime intervalStartTime = new DateTime(
                                    jobTimeRange.From.Year,
                                    jobTimeRange.From.Month,
                                    jobTimeRange.From.Day,
                                    jobTimeRange.From.Hour,
                                    0,
                                    0,
                                    DateTimeKind.Utc);
                                DateTime intervalEndTime = intervalStartTime.AddHours(1);
                                chartPrimaryAxis.Legend.Remove();
                                chartPrimaryAxis.XAxis.MinValue = intervalStartTime.ToLocalTime().ToOADate();
                                chartPrimaryAxis.XAxis.MaxValue = intervalEndTime.ToLocalTime().ToOADate();
                                chartPrimaryAxis.XAxis.MajorUnit = FIVE_MINUTES;
                            }
                            chart = chartPrimaryAxis;

                            // Create secondary axis only if needed
                            if (metricExtractMapping.Axis == METRIC_GRAPH_AXIS_SECONDARY)
                            {
                                if (chartSecondaryAxis == null)
                                {
                                    chartSecondaryAxis = chartPrimaryAxis.PlotArea.ChartTypes.Add(eChartType.XYScatterLinesNoMarkers);
                                    chartSecondaryAxis.UseSecondaryAxis = true;
                                    chartSecondaryAxis.Legend.Remove();
                                    chartSecondaryAxis.XAxis.MinValue = chartPrimaryAxis.XAxis.MinValue;
                                    chartSecondaryAxis.XAxis.MaxValue = chartPrimaryAxis.XAxis.MaxValue;
                                    chartSecondaryAxis.XAxis.MajorUnit = chartPrimaryAxis.XAxis.MajorUnit;
                                }
                                chart = chartSecondaryAxis;
                            }

                            // Output metrics
                            if (chart != null)
                            {
                                // The order of extracted metrics is sorted in EntityID, MetricsID, EventTime
                                // Therefore all the metrics for single item will stick together
                                // We want the full hour range indexed, including the beginning of the next hour in this hour's window
                                // therefore we have to advance 1 extra, but only for all the time ranges except the last one
                                int advanceToNextHourExtra = 1;
                                if (indexOfTimeRange == jobConfiguration.Input.HourlyTimeRanges.Count - 1)
                                {
                                    advanceToNextHourExtra = 0;
                                }

                                ExcelRangeBase rangeYValues = getSingleColumnRangeFromTable(tableMetrics, "Value", entityMetricValuesLocationForThisMetric.RowStart, entityMetricValuesLocationForThisMetric.RowEnd + advanceToNextHourExtra);
                                ExcelRangeBase rangeXTime = getSingleColumnRangeFromTable(tableMetrics, "EventTime", entityMetricValuesLocationForThisMetric.RowStart, entityMetricValuesLocationForThisMetric.RowEnd + advanceToNextHourExtra);
                                if (rangeYValues != null && rangeXTime != null)
                                {
                                    ExcelChartSerie series = chart.Series.Add(rangeYValues, rangeXTime);
                                    series.Header = metricExtractMapping.MetricName;
                                    ((ExcelScatterChartSerie)series).LineColor = getColorFromHexString(metricExtractMapping.LineColor);
                                }
                            }
                        }
                    }
                }
            }

            #endregion

            #region Output Events

            fromRow = REPORT_ENTITY_DETAILS_GRAPHS_SHEET_START_TABLE_AT + 1;

            int rowTableStart = fromRow;
            if (eventsFilteredList != null)
            {
                // Group by type and subtype to break the overall list in manageable chunks
                var eventsAllGroupedByType = eventsFilteredList.GroupBy(e => new { e.Type, e.SubType });
                List<List<Event>> eventsAllGrouped = new List<List<Event>>();

                // Group by the additional columns for some of the events
                foreach (var eventsGroup in eventsAllGroupedByType)
                {
                    switch (eventsGroup.Key.Type)
                    {
                        case "RESOURCE_POOL_LIMIT":
                            var eventsGroup_RESOURCE_POOL_LIMIT = eventsGroup.ToList().GroupBy(e => e.TierName);
                            foreach (var eventsGrouping in eventsGroup_RESOURCE_POOL_LIMIT)
                            {
                                eventsAllGrouped.Add(eventsGrouping.ToList());
                            }
                            break;

                        case "APPLICATION_ERROR":
                            var eventsGroup_APPLICATION_ERROR = eventsGroup.ToList().GroupBy(e => e.TierName);
                            foreach (var eventsGrouping in eventsGroup_APPLICATION_ERROR)
                            {
                                eventsAllGrouped.Add(eventsGrouping.ToList());
                            }
                            break;

                        case "DIAGNOSTIC_SESSION":
                            var eventsGroup_DIAGNOSTIC_SESSION = eventsGroup.ToList().GroupBy(e => new { e.TierName, e.BTName });
                            foreach (var eventsGrouping in eventsGroup_DIAGNOSTIC_SESSION)
                            {
                                eventsAllGrouped.Add(eventsGrouping.ToList());
                            }
                            break;

                        case "CUSTOM":
                            var eventsGroup_CUSTOM = eventsGroup.ToList().GroupBy(e => e.TierName);
                            foreach (var eventsGrouping in eventsGroup_CUSTOM)
                            {
                                eventsAllGrouped.Add(eventsGrouping.ToList());
                            }
                            break;

                        case "CLR_CRASH":
                            var eventsGroup_CLR_CRASH = eventsGroup.ToList().GroupBy(e => e.TierName);
                            foreach (var eventsGrouping in eventsGroup_CLR_CRASH)
                            {
                                eventsAllGrouped.Add(eventsGrouping.ToList());
                            }
                            break;

                        case "POLICY_OPEN_WARNING":
                        case "POLICY_OPEN_CRITICAL":
                        case "POLICY_CLOSE_WARNING":
                        case "POLICY_CLOSE_CRITICAL":
                        case "POLICY_UPGRADED":
                        case "POLICY_DOWNGRADED":
                        case "POLICY_CANCELED_WARNING":
                        case "POLICY_CANCELED_CRITICAL":
                        case "POLICY_CONTINUES_CRITICAL":
                        case "POLICY_CONTINUES_WARNING":
                            var eventsGroup_POLICY_ALL = eventsGroup.ToList().GroupBy(e => new { e.TriggeredEntityName, e.TierName });
                            foreach (var eventsGrouping in eventsGroup_POLICY_ALL)
                            {
                                eventsAllGrouped.Add(eventsGrouping.ToList());
                            }
                            break;

                        default:
                            eventsAllGrouped.Add(eventsGroup.ToList());
                            break;
                    }
                }

                // At this point we have the events partitioned just the way we want them
                // Each entry is guaranteed to have at least one item

                sheet.Cells[fromRow, 1].Value = "Type";
                sheet.Cells[fromRow, 2].Value = "SubType";
                sheet.Cells[fromRow, 3].Value = EntityTier.ENTITY_TYPE;
                sheet.Cells[fromRow, 4].Value = "BT";
                sheet.Cells[fromRow, 5].Value = "Trigger";

                fromRow++;
                for (int i = 0; i < eventsAllGrouped.Count; i++)
                {
                    int toRow = fromRow;

                    // Go through each hour range at a time
                    for (int j = 0; j < jobConfiguration.Input.HourlyTimeRanges.Count; j++)
                    {
                        JobTimeRange jobTimeRange = jobConfiguration.Input.HourlyTimeRanges[j];

                        List<Event> eventsInThisTimeRangeList = eventsAllGrouped[i].Where(e => e.OccurredUtc >= jobTimeRange.From && e.OccurredUtc < jobTimeRange.To).ToList();

                        // Now we finally have all the events for this type in this hour. Output
                        int columnIndexTimeRangeStart = columnOffsetBegin + j * columnOffsetBetweenRanges + j * numCellsPerHourRange;
                        foreach (Event interestingEvent in eventsInThisTimeRangeList)
                        {
                            // Find Column
                            int columnInThisTimeRange = columnIndexTimeRangeStart + interestingEvent.OccurredUtc.Minute;
                            // Find Row
                            int rowToOutputThisEventTo = fromRow;
                            while (true)
                            {
                                if (sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Value == null && sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula == String.Empty)
                                {
                                    break;
                                }
                                else
                                {
                                    rowToOutputThisEventTo++;
                                }
                            }
                            if (rowToOutputThisEventTo > fromRow && rowToOutputThisEventTo > toRow)
                            {
                                toRow = rowToOutputThisEventTo;
                            }

                            int rowIndexOfThisEvent = eventsFilteredList.FindIndex(e => e.EventID == interestingEvent.EventID);

                            // Finally output the value
                            switch (interestingEvent.Severity)
                            {
                                case "INFO":
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""I"")", REPORT_ENTITY_DETAILS_SHEET_EVENTS, getRangeEventDataTableThisEvent(tableEvents, rowIndexOfThisEvent));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "InfoEventLinkStyle";
                                    break;
                                case "WARN":
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""W"")", REPORT_ENTITY_DETAILS_SHEET_EVENTS, getRangeEventDataTableThisEvent(tableEvents, rowIndexOfThisEvent));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "WarnEventLinkStyle";
                                    break;
                                case "ERROR":
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""E"")", REPORT_ENTITY_DETAILS_SHEET_EVENTS, getRangeEventDataTableThisEvent(tableEvents, rowIndexOfThisEvent));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "ErrorEventLinkStyle";
                                    break;
                                default:
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""?"")", REPORT_ENTITY_DETAILS_SHEET_EVENTS, getRangeEventDataTableThisEvent(tableEvents, rowIndexOfThisEvent));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "ErrorEventLinkStyle";
                                    break;
                            }

                            // Add tooltip
                            ExcelComment comment = sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].AddComment(interestingEvent.Summary, interestingEvent.EventID.ToString());
                            comment.AutoFit = true;

                            // Is there more than one event in this time range
                            if (rowToOutputThisEventTo > fromRow)
                            {
                                // Yes, then indicate that it has a few by underline
                                sheet.Cells[fromRow, columnInThisTimeRange].Style.Font.UnderLine = true;
                            }
                        }
                    }

                    // Output headings in the event heading columns columns
                    Event firstEvent = eventsAllGrouped[i][0];
                    for (int j = fromRow; j <= toRow; j++)
                    {
                        sheet.Cells[j, 1].Value = firstEvent.Type;
                        if (firstEvent.SubType != String.Empty) { sheet.Cells[j, 2].Value = firstEvent.SubType; }
                        if (firstEvent.TierName != String.Empty) { sheet.Cells[j, 3].Value = firstEvent.TierName; }
                        if (firstEvent.BTName != String.Empty) { sheet.Cells[j, 4].Value = firstEvent.BTName; }
                        if (firstEvent.TriggeredEntityName != String.Empty)
                        {
                            sheet.Cells[j, 5].Value = firstEvent.TriggeredEntityName;
                        }
                        else
                        {
                            if (firstEvent.NodeName != String.Empty)
                            {
                                sheet.Cells[j, 5].Value = firstEvent.NodeName;
                            }
                        }
                        sheet.Cells[j, 1].StyleName = "EventHeadingStyle";
                        sheet.Cells[j, 2].StyleName = "EventHeadingStyle";
                        sheet.Cells[j, 3].StyleName = "EventHeadingStyle";
                        sheet.Cells[j, 4].StyleName = "EventHeadingStyle";
                        sheet.Cells[j, 5].StyleName = "EventHeadingStyle";
                        if (j == fromRow)
                        {
                            sheet.Row(j).OutlineLevel = 1;
                        }
                        else if (j > fromRow)
                        {
                            sheet.Row(j).OutlineLevel = 2;
                        }
                    }

                    fromRow = toRow;
                    fromRow++;
                }
            }
            int rowTableEnd = fromRow - 1;

            if (rowTableStart < rowTableEnd)
            {
                // Insert the table
                range = sheet.Cells[rowTableStart, 1, rowTableEnd, 5];
                table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_EVENTS_IN_TIMELINE);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.None;
                //table.TableStyle = TableStyles.Medium2;
                table.ShowFilter = true;
                table.ShowTotal = false;
            }

            #endregion

            #region Output Snapshots

            fromRow++;
            rowTableStart = fromRow;

            if (snapshotsFilteredList != null)
            {
                // Group by Tier and BT to break list into manageable chunks
                //var snapshotsAllGroupedByType = snapshotsFilteredList.GroupBy(s => new { s.TierName, s.BTName });
                var snapshotsAllGroupedByType = snapshotsFilteredList.OrderBy(s => s.TierName).ThenBy(s => s.BTName).GroupBy(s => new { s.TierName, s.BTName });
                List<List<Snapshot>> snapshotsAllGrouped = new List<List<Snapshot>>();

                // Group by the user experience
                foreach (var snapshotsGroup in snapshotsAllGroupedByType)
                {
                    List<Snapshot> snapshotsList = snapshotsGroup.ToList().Where(s => s.UserExperience == SNAPSHOT_UX_NORMAL).ToList();
                    if (snapshotsList.Count > 0) { snapshotsAllGrouped.Add(snapshotsList); }
                    snapshotsList = snapshotsGroup.ToList().Where(s => s.UserExperience == SNAPSHOT_UX_SLOW).ToList();
                    if (snapshotsList.Count > 0) { snapshotsAllGrouped.Add(snapshotsList); }
                    snapshotsList = snapshotsGroup.ToList().Where(s => s.UserExperience == SNAPSHOT_UX_VERY_SLOW).ToList();
                    if (snapshotsList.Count > 0) { snapshotsAllGrouped.Add(snapshotsList); }
                    snapshotsList = snapshotsGroup.ToList().Where(s => s.UserExperience == SNAPSHOT_UX_STALL).ToList();
                    if (snapshotsList.Count > 0) { snapshotsAllGrouped.Add(snapshotsList); }
                    snapshotsList = snapshotsGroup.ToList().Where(s => s.UserExperience == SNAPSHOT_UX_ERROR).ToList();
                    if (snapshotsList.Count > 0) { snapshotsAllGrouped.Add(snapshotsList); }
                }

                // At this point we have the snapshots partitioned just the way we want them
                // Each entry is guaranteed to have at least one item

                sheet.Cells[fromRow, 1].Value = EntityTier.ENTITY_TYPE;
                sheet.Cells[fromRow, 2].Value = "BT";
                sheet.Cells[fromRow, 3].Value = " ";
                sheet.Cells[fromRow, 4].Value = "  ";
                sheet.Cells[fromRow, 5].Value = "Experience";

                fromRow++;
                for (int i = 0; i < snapshotsAllGrouped.Count; i++)
                {
                    int toRow = fromRow;

                    // Go through each hour range at a time
                    for (int j = 0; j < jobConfiguration.Input.HourlyTimeRanges.Count; j++)
                    {
                        JobTimeRange jobTimeRange = jobConfiguration.Input.HourlyTimeRanges[j];

                        List<Snapshot> snapshotsInThisTimeRangeList = snapshotsAllGrouped[i].Where(s => s.OccurredUtc >= jobTimeRange.From && s.OccurredUtc < jobTimeRange.To).ToList();

                        // Now we finally have all the events for this type in this hour. Output
                        int columnIndexTimeRangeStart = columnOffsetBegin + j * columnOffsetBetweenRanges + j * numCellsPerHourRange;
                        foreach (Snapshot interestingSnapshot in snapshotsInThisTimeRangeList)
                        {
                            // Find Column
                            int columnInThisTimeRange = columnIndexTimeRangeStart + interestingSnapshot.OccurredUtc.Minute;
                            // Find Row
                            int rowToOutputThisEventTo = fromRow;
                            while (true)
                            {
                                if (sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Value == null && sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula == String.Empty)
                                {
                                    break;
                                }
                                else
                                {
                                    rowToOutputThisEventTo++;
                                }
                            }
                            if (rowToOutputThisEventTo > fromRow && rowToOutputThisEventTo > toRow)
                            {
                                toRow = rowToOutputThisEventTo;
                            }

                            // Finally output the value
                            int rowIndexOfThisSnapshot = snapshotsFilteredList.FindIndex(s => s.RequestID == interestingSnapshot.RequestID);
                            switch (interestingSnapshot.UserExperience)
                            {
                                case SNAPSHOT_UX_NORMAL:
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""N"")", REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS, getRangeEventDataTableThisEvent(tableSnapshots, rowIndexOfThisSnapshot));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "NormalSnapshotLinkStyle";
                                    break;
                                case SNAPSHOT_UX_SLOW:
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""S"")", REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS, getRangeEventDataTableThisEvent(tableSnapshots, rowIndexOfThisSnapshot));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "SlowSnapshotLinkStyle";
                                    break;
                                case SNAPSHOT_UX_VERY_SLOW:
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""V"")", REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS, getRangeEventDataTableThisEvent(tableSnapshots, rowIndexOfThisSnapshot));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "VerySlowSnapshotLinkStyle";
                                    break;
                                case SNAPSHOT_UX_STALL:
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""X"")", REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS, getRangeEventDataTableThisEvent(tableSnapshots, rowIndexOfThisSnapshot));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "StallSnapshotLinkStyle";
                                    break;
                                case SNAPSHOT_UX_ERROR:
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].Formula = String.Format(@"=HYPERLINK(""#'{0}'!{1}"", ""E"")", REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS, getRangeEventDataTableThisEvent(tableSnapshots, rowIndexOfThisSnapshot));
                                    sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].StyleName = "ErrorSnapshotLinkStyle";
                                    break;
                                default:
                                    break;
                            }

                            // Add tooltip with core details
                            ExcelComment comment = sheet.Cells[rowToOutputThisEventTo, columnInThisTimeRange].AddComment(
                                String.Format("Duration: {0}\nURL: {1}\nSegments: {2}\nCall Graph: {3}\nCall Chain:\n{4}",
                                    interestingSnapshot.Duration,
                                    interestingSnapshot.URL,
                                    interestingSnapshot.NumSegments,
                                    interestingSnapshot.CallGraphType,
                                    interestingSnapshot.CallChains),
                                interestingSnapshot.RequestID);

                            if (interestingSnapshot.UserExperience == SNAPSHOT_UX_ERROR)
                            {
                                comment.Text = String.Format("{0}\n{1}", comment.Text, interestingSnapshot.TakenSummary);
                            }

                            comment.AutoFit = true;

                            // Is there more than one event in this time range
                            if (rowToOutputThisEventTo > fromRow)
                            {
                                // Yes, then indicate that it has a few by underline
                                sheet.Cells[fromRow, columnInThisTimeRange].Style.Font.UnderLine = true;
                            }
                        }
                    }

                    // Output headings in the event heading columns columns
                    Snapshot firstSnapshot = snapshotsAllGrouped[i][0];
                    for (int j = fromRow; j <= toRow; j++)
                    {
                        sheet.Cells[j, 1].Value = firstSnapshot.TierName;
                        sheet.Cells[j, 2].Value = firstSnapshot.BTName;
                        sheet.Cells[j, 5].Value = firstSnapshot.UserExperience;
                        sheet.Cells[j, 1].StyleName = "EventHeadingStyle";
                        sheet.Cells[j, 2].StyleName = "EventHeadingStyle";
                        sheet.Cells[j, 5].StyleName = "EventHeadingStyle";
                        if (j == fromRow)
                        {
                            sheet.Row(j).OutlineLevel = 1;
                        }
                        else if (j > fromRow)
                        {
                            sheet.Row(j).OutlineLevel = 2;
                        }
                    }

                    fromRow = toRow;
                    fromRow++;
                }
            }
            rowTableEnd = fromRow - 1;

            if (rowTableStart < rowTableEnd)
            {
                // Insert the table
                range = sheet.Cells[rowTableStart, 1, rowTableEnd, 5];
                table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_SNAPSHOTS_IN_TIMELINE);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.None;
                //table.TableStyle = TableStyles.Medium2;
                table.ShowFilter = true;
                table.ShowTotal = false;
            }

            sheet.OutLineSummaryBelow = false;
            sheet.OutLineSummaryRight = true;

            #endregion

            #endregion
        }

        private static bool finalizeAndSaveIndividualEntityMetricReport(ExcelPackage excelEntityDetail, string entityType, string reportFilePath)
        {
            logger.Info("Finalize Entity Metrics Report File {0}", reportFilePath);

            ExcelWorksheet sheet;
            ExcelRangeBase range;
            ExcelTable table;

            #region Summary sheet

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_SUMMARY];

            if (sheet.Dimension.Rows > REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT)
            {
                table = sheet.Tables[REPORT_ENTITY_DETAILS_TABLE_ENTITY_HOURLY];
                if (table != null)
                {
                    table.ShowHeader = true;
                    table.TableStyle = TableStyles.Medium2;
                    table.ShowFilter = true;
                    table.ShowTotal = false;
                }
                table = sheet.Tables[REPORT_ENTITY_DETAILS_TABLE_ENTITY_FULL];
                if (table != null)
                {
                    table.ShowHeader = true;
                    table.TableStyle = TableStyles.Medium2;
                    table.ShowFilter = true;
                    table.ShowTotal = false;

                    adjustColumnsOfEntityRowTableInMetricReport(entityType, sheet, table);

                    // Remove DetailLink column because this document would be pointing to relative location from the overall metric reports, and the link would be invalid
                    if (table.Columns["DetailLink"] != null)
                    {
//                        sheet.Column(table.Columns["DetailLink"].Position + 1).Width = 1;
                        sheet.DeleteColumn(table.Columns["DetailLink"].Position + 1);
                    }
                }
            }

            #endregion

            #region Timeline sheet

            sheet.Column(1).Width = 12;
            sheet.Column(2).Width = 12;
            sheet.Column(3).Width = 12;
            sheet.Column(4).Width = 12;
            sheet.Column(5).Width = 12;

            #endregion

            #region Activity Flow sheet

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_ACTIVITYGRID];

            if (sheet.Dimension.Rows > REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT)
            {
                range = sheet.Cells[REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT, 1, sheet.Dimension.Rows, sheet.Dimension.Columns];
                table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_ACTIVITY_GRID);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.Medium2;
                table.ShowFilter = true;
                table.ShowTotal = false;

                sheet.Column(table.Columns["Controller"].Position + 1).Width = 20;
                sheet.Column(table.Columns["ApplicationName"].Position + 1).Width = 20;
                sheet.Column(table.Columns["CallType"].Position + 1).Width = 10;
                sheet.Column(table.Columns["FromName"].Position + 1).Width = 35;
                sheet.Column(table.Columns["ToName"].Position + 1).Width = 35;
                sheet.Column(table.Columns["From"].Position + 1).Width = 25;
                sheet.Column(table.Columns["To"].Position + 1).Width = 25;
                sheet.Column(table.Columns["FromUtc"].Position + 1).Width = 20;
                sheet.Column(table.Columns["ToUtc"].Position + 1).Width = 20;
            }

            #endregion

            #region Events sheet

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_EVENTS];

            if (sheet.Dimension.Rows > REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT)
            {
                table = sheet.Tables[REPORT_ENTITY_DETAILS_TABLE_EVENTS];
                if (table != null)
                {
                    table.ShowHeader = true;
                    table.TableStyle = TableStyles.Medium2;
                    table.ShowFilter = true;
                    table.ShowTotal = false;

                    sheet.Column(table.Columns["Controller"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["ApplicationName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["EventID"].Position + 1).Width = 25;
                    sheet.Column(table.Columns["Occurred"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["OccurredUtc"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["Summary"].Position + 1).Width = 35;
                    sheet.Column(table.Columns["Type"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["SubType"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["TierName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["NodeName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["BTName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["TriggeredEntityType"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["TriggeredEntityName"].Position + 1).Width = 20;
                }
            }

            #endregion

            #region Snapshots sheet

            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_ENTITY_DETAILS_SHEET_SNAPSHOTS];
            if (sheet.Dimension.Rows > REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT)
            {
                table = sheet.Tables[REPORT_ENTITY_DETAILS_TABLE_SNAPSHOTS];
                if (table != null)
                {
                    table.ShowHeader = true;
                    table.TableStyle = TableStyles.Medium2;
                    table.ShowFilter = true;
                    table.ShowTotal = false;

                    sheet.Column(table.Columns["Controller"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["ApplicationName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["TierName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["NodeName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["BTName"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["UserExperience"].Position + 1).Width = 10;
                    sheet.Column(table.Columns["RequestID"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["Occurred"].Position + 1).Width = 20;
                    sheet.Column(table.Columns["OccurredUtc"].Position + 1).Width = 20;

                    ExcelAddress cfAddressUserExperience = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["UserExperience"].Position + 1, sheet.Dimension.Rows, table.Columns["UserExperience"].Position + 1);
                    addUserExperienceConditionalFormatting(sheet, cfAddressUserExperience);

                    ExcelAddress cfAddressDuration = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["Duration"].Position + 1, sheet.Dimension.Rows, table.Columns["Duration"].Position + 1);
                    var cfDuration = sheet.ConditionalFormatting.AddThreeColorScale(cfAddressDuration);
                    cfDuration.LowValue.Color = colorGreenFor3ColorScales;
                    cfDuration.MiddleValue.Type = eExcelConditionalFormattingValueObjectType.Percentile;
                    cfDuration.MiddleValue.Value = 70;
                    cfDuration.MiddleValue.Color = colorYellowFor3ColorScales;
                    cfDuration.HighValue.Color = colorRedFor3ColorScales;

                    ExcelAddress cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumErrors"].Position + 1, sheet.Dimension.Rows, table.Columns["NumErrors"].Position + 1);
                    var cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCallGraphs"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCallGraphs"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCalledBackends"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCalledBackends"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCalledTiers"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCalledTiers"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCalledApplications"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCalledApplications"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCallsToBackends"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCallsToBackends"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCallsToTiers"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCallsToTiers"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumCallsToApplications"].Position + 1, sheet.Dimension.Rows, table.Columns["NumCallsToApplications"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumSEPs"].Position + 1, sheet.Dimension.Rows, table.Columns["NumSEPs"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumHTTPDCs"].Position + 1, sheet.Dimension.Rows, table.Columns["NumHTTPDCs"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);

                    cfAddressNum = new ExcelAddress(REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT + 1, table.Columns["NumMIDCs"].Position + 1, sheet.Dimension.Rows, table.Columns["NumMIDCs"].Position + 1);
                    cfNum = sheet.ConditionalFormatting.AddDatabar(cfAddressNum, colorLightBlueForDatabars);
                }
            }

            #endregion

            #region TOC sheet

            // TOC sheet again
            sheet = excelEntityDetail.Workbook.Worksheets[REPORT_SHEET_TOC];
            sheet.Cells[1, 1].Value = "Sheet Name";
            sheet.Cells[1, 2].Value = "# Entities";
            sheet.Cells[1, 3].Value = "Link";
            int rowNum = 1;
            foreach (ExcelWorksheet s in excelEntityDetail.Workbook.Worksheets)
            {
                rowNum++;
                sheet.Cells[rowNum, 1].Value = s.Name;
                sheet.Cells[rowNum, 3].Formula = String.Format(@"=HYPERLINK(""#'{0}'!A1"", ""<Go>"")", s.Name);
                sheet.Cells[rowNum, 3].StyleName = "HyperLinkStyle";
                if (s.Tables.Count == 1)
                {
                    sheet.Cells[rowNum, 2].Value = s.Tables[0].Address.Rows - 1;
                }
                else if (s.Tables.Count > 0)
                {
                    sheet.Cells[rowNum, 2].Value = String.Format("{0} tables", s.Tables.Count);
                }
            }
            range = sheet.Cells[1, 1, sheet.Dimension.Rows, sheet.Dimension.Columns];
            table = sheet.Tables.Add(range, REPORT_ENTITY_DETAILS_TABLE_TOC);
            table.ShowHeader = true;
            table.TableStyle = TableStyles.Medium2;
            table.ShowFilter = true;
            table.ShowTotal = false;

            sheet.Column(table.Columns["Sheet Name"].Position + 1).Width = 25;
            sheet.Column(table.Columns["# Entities"].Position + 1).Width = 25;

            #endregion

            #region Save file 

            // Report files
            logger.Info("Saving Excel report {0}", reportFilePath);
            //loggerConsole.Info("Saving Excel report {0}", reportFilePath);
            Console.Write(".");

            string folderPath = Path.GetDirectoryName(reportFilePath);
            if (Directory.Exists(folderPath) == false)
            {
                Directory.CreateDirectory(folderPath);
            }

            try
            {
                // Save full report Excel files
                excelEntityDetail.SaveAs(new FileInfo(reportFilePath));
            }
            catch (InvalidOperationException ex)
            {
                logger.Warn("Unable to save Excel file {0}", reportFilePath);
                logger.Warn(ex);
                loggerConsole.Warn("Unable to save Excel file {0}", reportFilePath);

                return false;
            }

            #endregion

            return true;
        }

        private void fillMetricValueTablesForEntityType(
            ExcelPackage excelReport,
            string sheetName,
            List<MetricExtractMapping> entityMetricExtractMappingList,
            JobTarget jobTarget,
            string entityFolderName)
        {
            ExcelWorksheet sheetMetrics = excelReport.Workbook.Worksheets[sheetName];

            int fromRow = REPORT_ENTITY_DETAILS_LIST_SHEET_START_TABLE_AT;
            int fromColumn = 1;

            // Load each of the metrics in the mapping table that apply to this entity type
            foreach (MetricExtractMapping metricExtractMapping in entityMetricExtractMappingList)
            {
                string metricsValuesFilePath = FilePathMap.MetricValuesIndexFilePath(jobTarget, entityFolderName, metricExtractMapping.FolderName);
                if (File.Exists(metricsValuesFilePath) == true)
                {
                    ExcelRangeBase range = EPPlusCSVHelper.ReadCSVFileIntoExcelRange(metricsValuesFilePath, 0, sheetMetrics, fromRow, fromColumn);
                    if (range != null)
                    {
                        if (range.Rows == 1)
                        {
                            // If there was no data in the table, adjust the range to have at least one blank line, otherwise Excel thinks table is corrupt
                            range = sheetMetrics.Cells[range.Start.Row, range.Start.Column, range.End.Row + 1, range.End.Column];
                        }
                        ExcelTable table = sheetMetrics.Tables.Add(range, String.Format(REPORT_ENTITY_DETAILS_METRIC_TABLE_METRIC_VALUES, entityFolderName, metricExtractMapping.FolderName));
                        table.ShowHeader = true;
                        table.TableStyle = TableStyles.Medium2;
                        table.ShowFilter = true;
                        table.ShowTotal = false;

                        sheetMetrics.Column(table.Columns["Controller"].Position + fromColumn).Width = 15;
                        sheetMetrics.Column(table.Columns["ApplicationName"].Position + fromColumn).Width = 15;
                        sheetMetrics.Column(table.Columns["EntityName"].Position + fromColumn).Width = 15;
                        sheetMetrics.Column(table.Columns["EntityType"].Position + fromColumn).Width = 15;
                        sheetMetrics.Column(table.Columns["MetricName"].Position + fromColumn).Width = 15;
                        sheetMetrics.Column(table.Columns["EventTimeStamp"].Position + fromColumn).Width = 20;

                        fromColumn = fromColumn + range.Columns + 1;
                    }
                }
            }
        }

        private static ExcelRangeBase getRangeEventDataTableThisEvent(ExcelTable table, int rowIndex)
        {
            return table.WorkSheet.Cells[
                table.Address.Start.Row + rowIndex + 1,
                table.Address.Start.Column,
                table.Address.Start.Row + rowIndex + 1,
                table.Address.End.Column];
        }
    }
}

// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry.Exporter.GoogleCloud.Implementation;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenTelemetry.Exporter.GoogleCloud.Tests;

public class GoogleCloudMonitoringExporterTests
{
    static GoogleCloudMonitoringExporterTests()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(listener);
    }

    [Fact]
    public void GoogleCloudMonitoringExporter_CustomActivityProcessor()
    {
        const string ActivitySourceName = "stackdriver.test";
        var requestId = Guid.NewGuid();
        var testActivityProcessor = new TestActivityProcessor();

        var startCalled = false;
        var endCalled = false;

        testActivityProcessor.StartAction =
            (a) =>
            {
                startCalled = true;
            };

        testActivityProcessor.EndAction =
            (a) =>
            {
                endCalled = true;
            };

        var openTelemetrySdk = Sdk.CreateTracerProviderBuilder()
            .AddSource(ActivitySourceName)
            .AddProcessor(testActivityProcessor)
            .UseGoogleCloudExporter("test").Build();

        var source = new ActivitySource(ActivitySourceName);
        var activity = source.StartActivity("Test Activity");
        activity?.Stop();

        Assert.True(startCalled);
        Assert.True(endCalled);
    }

    [Fact]
    public void GoogleCloudMonitoringExporter_WithServiceNameMetadata()
    {
        const string ActivitySourceName = "stackdriver.test";

        var traceClient = new TestTraceServiceClient(throwException: false);
        var activityExporter = new GoogleCloudTraceExporter("test_project", traceClient);

        var openTelemetrySdk = Sdk.CreateTracerProviderBuilder()
            .AddSource(ActivitySourceName)
            .ConfigureResource(r => r.AddService("test-service", "2.3.4"))
            .AddProcessor(new BatchActivityExportProcessor(activityExporter))
            .Build();

        using var source = new ActivitySource(ActivitySourceName);
        var activity = source.StartActivity("Test Activity");
        activity?.Stop();
        openTelemetrySdk.ForceFlush();
        Assert.True(traceClient.Spans.Count > 0);
        Assert.True(traceClient.Spans.All(s => s.Attributes.AttributeMap.ContainsKey(ResourceSemanticConventions.AttributeServiceName)));
    }

    [Fact]
    public void GoogleCloudMonitoringExporter_TraceClientThrows_ExportResultFailure()
    {
        Exception? exception;
        var result = ExportResult.Success;
        var exportedItems = new List<Activity>();
        const string ActivitySourceName = "stackdriver.test";
        var source = new ActivitySource(ActivitySourceName);
        var traceClient = new TestTraceServiceClient(throwException: true);
        var activityExporter = new GoogleCloudTraceExporter("test", traceClient);

        var processor = new BatchActivityExportProcessor(new InMemoryExporter<Activity>(exportedItems));

        for (var i = 0; i < 10; i++)
        {
            using var activity = CreateTestActivity();
            processor.OnEnd(activity);
        }

        processor.Shutdown();

        var batch = new Batch<Activity>([.. exportedItems], exportedItems.Count);
        RunTest(batch);

        void RunTest(Batch<Activity> batch)
        {
            exception = Record.Exception(() =>
            {
                result = activityExporter.Export(batch);
            });
        }

        Assert.Null(exception);
        Assert.StrictEqual(ExportResult.Failure, result);
    }

    [Fact]
    public void GoogleCloudMonitoringExporter_TraceClientDoesNotTrow_ExportResultSuccess()
    {
        Exception? exception;
        var result = ExportResult.Failure;
        var exportedItems = new List<Activity>();
        const string ActivitySourceName = "stackdriver.test";
        var source = new ActivitySource(ActivitySourceName);
        var traceClient = new TestTraceServiceClient(throwException: false);
        var activityExporter = new GoogleCloudTraceExporter("test", traceClient);

        var processor = new BatchActivityExportProcessor(new InMemoryExporter<Activity>(exportedItems));

        for (var i = 0; i < 10; i++)
        {
            using var activity = CreateTestActivity();
            processor.OnEnd(activity);
        }

        processor.Shutdown();

        var batch = new Batch<Activity>([.. exportedItems], exportedItems.Count);
        RunTest(batch);

        void RunTest(Batch<Activity> batch)
        {
            exception = Record.Exception(() =>
            {
                result = activityExporter.Export(batch);
            });
        }

        Assert.Null(exception);
        Assert.StrictEqual(ExportResult.Success, result);
    }

    internal static Activity CreateTestActivity(
        bool setAttributes = true,
        Dictionary<string, object>? additionalAttributes = null,
        bool addEvents = true,
        bool addLinks = true,
        ActivityKind kind = ActivityKind.Client)
    {
        var startTimestamp = DateTime.UtcNow;
        var endTimestamp = startTimestamp.AddSeconds(60);
        var eventTimestamp = DateTime.UtcNow;
        var traceId = ActivityTraceId.CreateFromString("e8ea7e9ac72de94e91fabc613f9686b2".AsSpan());

        var parentSpanId = ActivitySpanId.CreateFromBytes([12, 23, 34, 45, 56, 67, 78, 89]);

        var attributes = new Dictionary<string, object?>
        {
            { "stringKey", "value" },
            { "longKey", 1L },
            { "longKey2", 1 },
            { "doubleKey", 1D },
            { "doubleKey2", 1F },
            { "boolKey", true },
            { "nullKey", null },
            { "http.url", null },
        };
        if (additionalAttributes != null)
        {
            foreach (var attribute in additionalAttributes)
            {
                attributes.Add(attribute.Key, attribute.Value);
            }
        }

        var events = new List<ActivityEvent>
        {
            new(
                "Event1",
                eventTimestamp,
                new ActivityTagsCollection
                {
                    { "key", "value" },
                }),
            new(
                "Event2",
                eventTimestamp,
                new ActivityTagsCollection
                {
                    { "key", "value" },
                }),
        };

        var linkedSpanId = ActivitySpanId.CreateFromString("888915b6286b9c41".AsSpan());

        var activitySource = new ActivitySource(nameof(CreateTestActivity));

        var tags = setAttributes ?
            attributes.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value?.ToString()))
            : null;
        var links = addLinks ?
            new[]
            {
                new ActivityLink(new ActivityContext(
                    traceId,
                    linkedSpanId,
                    ActivityTraceFlags.Recorded)),
            }
            : null;

        var activity = activitySource.StartActivity(
            "Name",
            kind,
            parentContext: new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded),
            tags,
            links,
            startTime: startTimestamp)!;

        if (addEvents)
        {
            foreach (var evnt in events)
            {
                activity.AddEvent(evnt);
            }
        }

        activity.SetEndTime(endTimestamp);
        activity.Stop();

        return activity;
    }
}

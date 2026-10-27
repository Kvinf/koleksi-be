using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.AspNetCoreServer.Internal;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The synthetic event an EventBridge rule hands the API Lambda to trigger the sweep.
/// </summary>
/// <remarks>
/// <para>
/// EventBridge cannot send an HTTP request; it invokes the Lambda with a fixed JSON document. The
/// API Lambda is hosted as an HTTP API payload-v2 function (<c>AddAWSLambdaHosting(HttpApi)</c>), so
/// that document must be a payload-v2 event that the hosting library turns into
/// <c>POST /internal/sweep</c> carrying the secret header. Getting any field wrong would not fail a
/// build or a deploy; the schedule would simply do nothing.
/// </para>
/// <para>
/// <see cref="Event"/> is a copy of what <c>koleksi-infra/lib/app-stack.ts</c> passes to
/// <c>RuleTargetInput.fromObject</c> (same fields, the secret substituted). Change them together.
/// These tests push it through the library's own payload-v2 JSON deserialiser and its own request
/// marshaller (the code that runs in production), so the shape is checked against the library,
/// not against our memory of it.
/// </para>
/// </remarks>
public sealed class SweepEventShapeTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string Event = $$"""
        {
          "version": "2.0",
          "routeKey": "POST /internal/sweep",
          "rawPath": "/internal/sweep",
          "rawQueryString": "",
          "headers": {
            "x-koleksi-internal": "{{Secret}}",
            "user-agent": "Amazon/EventBridge"
          },
          "requestContext": {
            "accountId": "000000000000",
            "apiId": "eventbridge-sweep",
            "domainName": "eventbridge-sweep.invalid",
            "domainPrefix": "eventbridge-sweep",
            "http": {
              "method": "POST",
              "path": "/internal/sweep",
              "protocol": "HTTP/1.1",
              "sourceIp": "127.0.0.1",
              "userAgent": "Amazon/EventBridge"
            },
            "requestId": "eventbridge-sweep",
            "routeKey": "POST /internal/sweep",
            "stage": "$default",
            "time": "01/Jan/2026:00:00:00 +0000",
            "timeEpoch": 1767225600000
          },
          "isBase64Encoded": false,
          "body": ""
        }
        """;

    private static APIGatewayHttpApiV2ProxyRequest Deserialise()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Event));
        return new DefaultLambdaJsonSerializer().Deserialize<APIGatewayHttpApiV2ProxyRequest>(stream);
    }

    [Fact]
    public void The_library_reads_every_field_the_rule_sends()
    {
        var request = Deserialise();

        request.Version.ShouldBe("2.0");
        request.RouteKey.ShouldBe("POST /internal/sweep");
        request.RawPath.ShouldBe("/internal/sweep");
        request.RawQueryString.ShouldBeEmpty();
        request.Headers["x-koleksi-internal"].ShouldBe(Secret);
        request.IsBase64Encoded.ShouldBeFalse();
        request.Body.ShouldBeEmpty();

        request.RequestContext.Http.Method.ShouldBe("POST");
        request.RequestContext.Http.Path.ShouldBe("/internal/sweep");
        request.RequestContext.Http.Protocol.ShouldBe("HTTP/1.1");
        request.RequestContext.Http.SourceIp.ShouldBe("127.0.0.1");
        request.RequestContext.Http.UserAgent.ShouldBe("Amazon/EventBridge");
        request.RequestContext.Stage.ShouldBe("$default");
        request.RequestContext.RequestId.ShouldNotBeNullOrEmpty();
        request.RequestContext.ApiId.ShouldNotBeNullOrEmpty();
        request.RequestContext.AccountId.ShouldNotBeNullOrEmpty();
        request.RequestContext.DomainName.ShouldNotBeNullOrEmpty();
        request.RequestContext.TimeEpoch.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_hosting_library_turns_it_into_a_post_to_the_sweep_route_with_the_secret_header()
    {
        // The library's own marshaller: the code that builds the ASP.NET Core request from an
        // HTTP API event when this runs as a Lambda.
        var features = new InvokeFeatures();
        new Probe().Marshal(features, Deserialise());

        var request = (IHttpRequestFeature)features;
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe("/internal/sweep");
        request.QueryString.ShouldBeNullOrEmpty();
        request.Headers["x-koleksi-internal"].ToString().ShouldBe(Secret);

        // The remote address is what the global rate limiter partitions on.
        var remote = ((IHttpConnectionFeature)features).RemoteIpAddress;
        remote.ShouldNotBeNull();
        remote.ToString().ShouldBe("127.0.0.1");
    }

    /// <summary>Reaches the library's protected marshaller without starting a host.</summary>
    private sealed class Probe : APIGatewayHttpApiV2ProxyFunction
    {
        public Probe()
            : base(StartupMode.FirstRequest)
        {
        }

        public void Marshal(InvokeFeatures features, APIGatewayHttpApiV2ProxyRequest request) =>
            MarshallRequest(features, request, null!);

        protected override void Init(IWebHostBuilder builder)
        {
        }
    }
}

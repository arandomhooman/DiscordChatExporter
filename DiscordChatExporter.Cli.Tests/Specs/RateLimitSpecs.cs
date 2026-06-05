using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class RateLimitSpecs
{
    [Fact]
    public async Task Emits_rate_limit_events_for_retry_after_429_when_advisory_limits_are_disabled()
    {
        using var httpClient = new HttpClient(
            new QueueHttpMessageHandler([
                new HttpResponseMessage(HttpStatusCode.OK),
                CreateRateLimitResponse(TimeSpan.Zero),
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "id": "123456789012345678",
                          "username": "test-user",
                          "global_name": "Test User",
                          "discriminator": "0",
                          "avatar": null
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"
                    ),
                },
            ])
        );

        var discord = new DiscordClient(
            "test-token",
            RateLimitPreference.IgnoreAll,
            httpClient,
            (_, _) => ValueTask.CompletedTask
        );

        var states = new List<RateLimitState>();
        discord.RateLimitChanged += (_, state) => states.Add(state);

        var user = await discord.TryGetUserAsync(Snowflake.Parse("123456789012345678"));

        user.Should().NotBeNull();
        states
            .Should()
            .Equal(
                new RateLimitState(true, TimeSpan.FromSeconds(1)),
                new RateLimitState(false, TimeSpan.Zero)
            );
    }

    private static HttpResponseMessage CreateRateLimitResponse(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private sealed class QueueHttpMessageHandler(IReadOnlyCollection<HttpResponseMessage> responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("Unexpected HTTP request.");

            return Task.FromResult(response);
        }
    }
}

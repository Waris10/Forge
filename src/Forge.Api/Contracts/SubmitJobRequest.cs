using System.Text.Json;

namespace Forge.Api.Contracts;

/// <summary>
/// The shape of POST /jobs request body.
///
/// This is a distinct type from Forge.Core.Job on purpose: requests are the
/// external API contract; Job is the internal domain model. If the DB schema
/// changes, we shouldn't have to break every HTTP client. Keep the wire format
/// and the domain model independently evolvable.
/// </summary>
public record SubmitJobRequest(
    string JobType,
    JsonElement Payload,
    string? Queue = null,
    int? Priority = null,
    int? MaxAttempts = null,
    int? DelaySeconds = null,
    string? IdempotencyKey = null);

/// <summary>
/// The shape of the 202 Accepted response.
/// </summary>
public record SubmitJobResponse(Guid Id, string Status);
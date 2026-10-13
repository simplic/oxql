using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using OxQL.Core.Binding;

namespace OxQL.Core.Cursor;

/// <summary>
/// Encodes cursors as <c>base64url(payload) "." base64url(HMAC-SHA256)</c>: opaque, signed
/// with a key derived from the host's secret, and bound to the query through the bound
/// pipeline's fingerprint. Values travel as canonical extended JSON so their BSON type survives.
/// </summary>
public sealed class CursorCodec
{
    private const string Info = "oxql-cursor";
    private readonly byte[] key;

    /// <summary>Derives the signing key from the host's secret.</summary>
    /// <exception cref="InvalidOperationException">No secret is configured.</exception>
    public CursorCodec(string? signingSecret)
    {
        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new InvalidOperationException("OxQL needs a cursor signing key: configure OxQL:Cursor:SigningKey (the base package derives it from the auth token).");

        key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(signingSecret), 32, salt: null, info: Encoding.UTF8.GetBytes(Info));
    }

    /// <summary>Encodes a payload.</summary>
    public string Encode(CursorPayload payload)
    {
        var body = new JsonObject
        {
            ["f"] = payload.Fingerprint,
            ["m"] = payload.Mode == PagingMode.Keyset ? "k" : "o",
            ["o"] = payload.Offset,
            ["v"] = new JsonArray(payload.Fields.Select(field => (JsonNode)new JsonObject
            {
                ["p"] = field.Wire,
                ["d"] = field.Ascending ? "asc" : "desc",
                ["b"] = field.Value.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson }),
            }).ToArray()),
        };

        // Written only by a contract 2 page, so a contract 1 cursor is byte for byte what it was.
        if (payload.Served > 0)
            body["n"] = payload.Served;

        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());

        return Base64Url(bytes) + "." + Base64Url(HMACSHA256.HashData(key, bytes));
    }

    /// <summary><c>reason</c> of a cursor that is no cursor of this engine: not two base64url parts, or a body that does not read.</summary>
    public const string Malformed = "malformed";

    /// <summary><c>reason</c> of a cursor whose signature is not this host's: altered, or issued by a host with another signing key.</summary>
    public const string Signature = "signature";

    /// <summary><c>reason</c> of a cursor this host issued for another query: another entity, filter, sort or organisation.</summary>
    public const string OtherQuery = "query";

    /// <summary>Decodes and verifies a cursor; null when the signature, the form or the fingerprint does not match.</summary>
    public CursorPayload? Decode(string cursor, string expectedFingerprint) => Decode(cursor, expectedFingerprint, out _);

    /// <summary>
    /// Decodes and verifies a cursor; null when it does not verify, and then <paramref name="reason"/>
    /// says which of the three it is: <see cref="Malformed"/>, <see cref="Signature"/> or <see cref="OtherQuery"/>.
    /// </summary>
    public CursorPayload? Decode(string cursor, string expectedFingerprint, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        reason = Malformed;

        var dot = cursor.IndexOf('.');

        if (dot <= 0 || dot == cursor.Length - 1)
            return null;

        byte[] bytes;
        byte[] signature;

        try
        {
            bytes = FromBase64Url(cursor[..dot]);
            signature = FromBase64Url(cursor[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, bytes)))
        {
            reason = Signature;
            return null;
        }

        try
        {
            var body = JsonNode.Parse(bytes)?.AsObject();

            if (body is null)
                return null;

            if (body["f"]?.GetValue<string>() != expectedFingerprint)
            {
                reason = OtherQuery;
                return null;
            }

            var mode = body["m"]?.GetValue<string>() == "o" ? PagingMode.Offset : PagingMode.Keyset;
            var offset = body["o"]?.GetValue<int>() ?? 0;
            var fields = new List<CursorValue>();

            foreach (var entry in body["v"]?.AsArray() ?? [])
            {
                var field = entry!.AsObject();

                fields.Add(new CursorValue(
                    field["p"]!.GetValue<string>(),
                    field["d"]!.GetValue<string>() == "asc",
                    BsonSerializerExtensions.ParseCanonical(field["b"]!.GetValue<string>())));
            }

            var payload = new CursorPayload(expectedFingerprint, mode, fields, offset) { Served = body["n"]?.GetValue<long>() ?? 0 };

            reason = null;

            return payload;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or InvalidCastException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');

        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }

        return Convert.FromBase64String(output);
    }
}

internal static class BsonSerializerExtensions
{
    /// <summary>Parses one canonical extended JSON value.</summary>
    public static BsonValue ParseCanonical(string json)
    {
        var wrapped = BsonDocument.Parse("{\"v\":" + json + "}");

        return wrapped["v"];
    }
}

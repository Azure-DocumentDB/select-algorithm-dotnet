using System.Text.RegularExpressions;

namespace SelectAlgorithm.Models;

public sealed class ConfigurationException(string message) : Exception(message);
public sealed class IndexConflictException : Exception;
public sealed class DuplicateDocumentException : Exception;
public sealed class VerificationException : Exception;

/// <summary>Process environment only; no silent alternate source or unsafe default namespace.</summary>
public sealed record AppConfiguration(
    string MongoUri, string AllowedHost, string Database, string CollectionPrefix,
    string AuthMode, string Username, string Password,
    string DataFile, string QueryFile, bool AllowWrites, int TimeoutSeconds, int TopK = 5)
{
    public static AppConfiguration Read(Func<string, string?>? environment = null)
    {
        var get = environment ?? Environment.GetEnvironmentVariable;
        RejectRemovedKeys(get);
        string Value(string key, string fallback = "") => get(key) ?? fallback;
        var config = new AppConfiguration(
            Value("MONGO_URI"), Value("MONGO_ALLOWED_HOST"), Value("MONGO_DATABASE"),
            Value("MONGO_COLLECTION_PREFIX"), Value("MONGO_AUTH_MODE"), Value("MONGO_USERNAME"), Value("MONGO_PASSWORD"),
            DataPath(get), QueryPath(get), Value("ALLOW_DATABASE_WRITES") == "true",
            int.TryParse(Value("OPERATION_TIMEOUT_SECONDS", "300"), out var seconds) ? seconds : 0, ReadTopK(get));
        config.Validate();
        return config;
    }

    public static string DataPath(Func<string, string?> get) => get("VECTOR_DATA_FILE") ?? "data/Hotels_Vector.json";
    public static string QueryPath(Func<string, string?> get) => get("QUERY_VECTOR_FILE") ?? "data/query-vector.json";
    public static int ReadTopK(Func<string, string?> get)
    {
        // Previously documented key remains authoritative. The formerly undocumented alias is rejected.
        if (!int.TryParse(get("VectorSearch__TopK") ?? "5", out var k) || k is < 1 or > 50)
            throw new ConfigurationException("VectorSearch__TopK must be 1..50.");
        return k;
    }

    public static void RejectRemovedKeys(Func<string, string?> get)
    {
        foreach (var key in new[] { "VectorSearch__Query", "VectorSearch__Similarity", "QUERY_TEXT", "TOP_K",
            "DocumentDB__ClusterName", "DocumentDB__DatabaseName", "DocumentDB__LoadBatchSize",
            "DataFiles__WithVectors", "Embedding__EmbeddedField", "Embedding__Dimensions",
            "AzureOpenAI__Endpoint", "AzureOpenAI__EmbeddingModel" })
            if (get(key) is not null)
                throw new ConfigurationException("Removed configuration key is set; migrate using the README. No text/model query is supported.");
    }

    public void Validate()
    {
        ValidateNamespace(Database);
        if (!Regex.IsMatch(CollectionPrefix, @"\A[a-z][a-z0-9_]{0,47}\z", RegexOptions.CultureInvariant))
            throw new ConfigurationException("Set an explicit lowercase MONGO_COLLECTION_PREFIX.");
        if (AuthMode is not ("scram" or "oidc")) throw new ConfigurationException("Set MONGO_AUTH_MODE to scram or oidc.");
        if (AuthMode == "scram" && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)))
            throw new ConfigurationException("Native authentication requires MONGO_USERNAME and MONGO_PASSWORD.");
        if (AuthMode == "oidc" && (!string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(Username)))
            throw new ConfigurationException("Unset native MONGO_USERNAME and MONGO_PASSWORD for OIDC.");
        if (TimeoutSeconds is < 30 or > 600) throw new ConfigurationException("OPERATION_TIMEOUT_SECONDS must be 30..600.");
        if (TopK is < 1 or > 50) throw new ConfigurationException("VectorSearch__TopK must be 1..50.");
        if (string.IsNullOrWhiteSpace(DataFile) || string.IsNullOrWhiteSpace(QueryFile)) throw new ConfigurationException("Provide both fixture paths.");
    }

    public static void ValidateNamespace(string name)
    {
        if (name.Length > 63 || !Regex.IsMatch(name, @"\Avectorsearch_disposable_[a-z0-9_]{1,41}\z", RegexOptions.CultureInvariant))
            throw new ConfigurationException("Use an explicit vectorsearch_disposable_ database.");
    }
    public void RequireWrites()
    {
        if (!AllowWrites) throw new ConfigurationException("Set ALLOW_DATABASE_WRITES=true only for an approved disposable namespace.");
    }
    public string Collection(ComparisonCase item) => $"{CollectionPrefix}_{AuthMode}_{item.Algorithm.Suffix()}_{item.Metric.ToString().ToLowerInvariant()}";
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Tuning;

public static class PlanBuilder
{
    private const int MaxInt = int.MaxValue;
    public static Plan Build(Guid targetSessionId, long revision, DateTimeOffset createdUtc, DraftInputs inputs,
        IReadOnlyDictionary<string, PreferenceValue> current, int libtorrentMajor,
        IReadOnlyList<TargetInterface> interfaces, IReadOnlyDictionary<string, PreferenceValue>? baseline,
        PlanSelection[] selections)
    {
        if (targetSessionId == Guid.Empty || !CommandPayloads.SafeRevision(revision) || current is null || interfaces is null || selections is null)
            throw new QbittorrentException(ErrorCodes.InvalidOverride);
        if (selections.Any(s => s is null || string.IsNullOrWhiteSpace(s.GroupId) || s.Overrides is null)
            || selections.Select(s => s.GroupId).Distinct(StringComparer.Ordinal).Count() != selections.Length)
            throw new QbittorrentException(ErrorCodes.InvalidOverride);
        CommandPayloads.ValidateInputs(inputs);
        var selectionByGroup = selections.ToDictionary(s => s.GroupId, StringComparer.Ordinal);
        var settings = Calculator.Calculate(inputs);
        var blocks = new HashSet<string>(StringComparer.Ordinal);
        string? resolvedInterface = null;

        if (inputs.Network.UseVpn || inputs.Usage.Environment == EnvironmentProfile.Docker)
        {
            if (string.IsNullOrWhiteSpace(settings.NetworkInterface))
            {
                blocks.Add("VPN_INTERFACE_REQUIRED");
            }
            else
            {
                var matches = interfaces.Where(i => i is not null
                        && (string.Equals(i.Id, settings.NetworkInterface, StringComparison.Ordinal)
                            || string.Equals(i.Name, settings.NetworkInterface, StringComparison.Ordinal)))
                    .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
                if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Id))
                    blocks.Add(matches.Length == 0 ? "VPN_INTERFACE_MISSING" : "VPN_INTERFACE_AMBIGUOUS");
                else
                    resolvedInterface = matches[0].Id;
            }
        }
        if (resolvedInterface is not null) settings = settings with { NetworkInterface = resolvedInterface };

        var mapped = PreferenceMapping.MapForPlan(settings, current, libtorrentMajor);
        var omissions = mapped.Omissions.ToList();
        var portExcluded = selectionByGroup.TryGetValue("port", out var portSelection) && !portSelection.Selected;
        if (portExcluded)
            omissions = omissions.Select(o => o.ApiKey is "listen_port" or "random_port" ? o with { Required = false } : o).ToList();
        var missingInterface = blocks.Any(code => code.StartsWith("VPN_INTERFACE_", StringComparison.Ordinal));
        if (missingInterface && settings.NetworkInterface.Length > 0)
        {
            omissions.RemoveAll(o => o.ApiKey == "current_network_interface");
            omissions.Add(new("current_network_interface", true, blocks.First(code => code.StartsWith("VPN_INTERFACE_", StringComparison.Ordinal))));
        }

        var candidates = mapped.Candidates.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (missingInterface && !candidates.ContainsKey("current_network_interface"))
        {
            candidates["current_network_interface"] = new StringPreference(settings.NetworkInterface.Length > 0
                ? settings.NetworkInterface : inputs.Network.VpnInterface);
            omissions.RemoveAll(o => o.ApiKey == "current_network_interface");
            omissions.Add(new("current_network_interface", true,
                blocks.First(code => code.StartsWith("VPN_INTERFACE_", StringComparison.Ordinal))));
        }
        var supportByKey = new Dictionary<string, SupportStatus>(StringComparer.Ordinal);
        foreach (var key in candidates.Keys)
        {
            if (mapped.Values.ContainsKey(key)) supportByKey[key] = SupportStatus.Supported;
            else
            {
                var omission = omissions.First(o => o.ApiKey == key);
                supportByKey[key] = omission.ReasonCode == "SETTING_TYPE_INCOMPATIBLE"
                    ? SupportStatus.Incompatible
                    : omission.Required ? SupportStatus.MissingRequired : SupportStatus.MissingOptional;
                if (omission.Required) blocks.Add("REQUIRED_SETTING_UNAVAILABLE");
            }
        }
        foreach (var omission in omissions.Where(o => o.Required)) blocks.Add("REQUIRED_SETTING_UNAVAILABLE");
        if (missingInterface) supportByKey["current_network_interface"] = SupportStatus.MissingRequired;

        var portIncomplete = candidates.ContainsKey("listen_port") && candidates.ContainsKey("random_port")
            && (!mapped.Values.ContainsKey("listen_port") || !mapped.Values.ContainsKey("random_port"));
        if (portIncomplete && !portExcluded) blocks.Add("PORT_GROUP_UNAVAILABLE");

        foreach (var groupId in selectionByGroup.Keys)
            if (!candidates.Keys.Any(key => Group(key) == groupId)) throw new QbittorrentException(ErrorCodes.InvalidOverride);

        var recommendations = new List<Recommendation>();
        foreach (var pair in candidates.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var key = pair.Key;
            var value = pair.Value;
            var group = Group(key);
            selectionByGroup.TryGetValue(group, out var selection);
            var selected = selection?.Selected ?? true;
            var editable = Editable(key, value);
            var proposed = value;
            if (selection is not null && selection.Overrides.TryGetValue(key, out var overridden))
            {
                if (!selected || !editable) throw new QbittorrentException(ErrorCodes.InvalidOverride);
                proposed = ValidateOverride(key, overridden, inputs.Network.UseVpn || inputs.Usage.Environment == EnvironmentProfile.Docker,
                    interfaces);
            }
            var currentValue = current.GetValueOrDefault(key);
            var type = ValueType(key, value);
            var allowedRange = Range(key);
            var allowedValues = AllowedValues(key, interfaces);
            var hasOverride = selection is not null && selection.Overrides.ContainsKey(key);
            var reason = hasOverride
                ? new LocalizedMessage("recommendations.reason.manualOverride", new Dictionary<string, MessageParameter>())
                : settings.Explanations.TryGetValue(ExplanationProperty(key), out var explanation)
                    ? new LocalizedMessage(explanation.Code, explanation.Parameters)
                    : new LocalizedMessage("recommendations.reason.heuristic", new Dictionary<string, MessageParameter>());
            recommendations.Add(new("recommendation-" + key, group, key, Category(key),
                "recommendations." + key, reason,
                Evidence.Heuristic, currentValue, proposed, Unit(key), type, editable, allowedRange, allowedValues,
                supportByKey.GetValueOrDefault(key, SupportStatus.Incompatible), selected, Cautions(key, settings.Warnings)));
        }

        foreach (var selection in selections)
            foreach (var key in selection.Overrides.Keys)
                if (!candidates.ContainsKey(key) || Group(key) != selection.GroupId)
                    throw new QbittorrentException(ErrorCodes.InvalidOverride);

        var proposedValues = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        foreach (var recommendation in recommendations)
        {
            if (!recommendation.Selected || recommendation.SupportStatus != SupportStatus.Supported || missingInterface && recommendation.ApiKey == "current_network_interface"
                || portIncomplete && recommendation.GroupId == "port") continue;
            proposedValues.Add(recommendation.ApiKey, recommendation.ProposedValue);
        }
        if ((inputs.Network.UseVpn || inputs.Usage.Environment == EnvironmentProfile.Docker)
            && selectionByGroup.TryGetValue("current_network_interface", out var interfaceSelection)
            && !interfaceSelection.Selected && !Equals(current.GetValueOrDefault("current_network_interface"), new StringPreference(resolvedInterface ?? string.Empty)))
            blocks.Add("VPN_BINDING_DESELECTED");
        var finalLow = proposedValues.GetValueOrDefault("send_buffer_low_watermark") ?? current.GetValueOrDefault("send_buffer_low_watermark");
        var finalHigh = proposedValues.GetValueOrDefault("send_buffer_watermark") ?? current.GetValueOrDefault("send_buffer_watermark");
        if (finalLow is not null && finalHigh is not null
            && finalLow is IntegerPreference low && finalHigh is IntegerPreference high
            && low.Value > high.Value)
            throw new QbittorrentException(ErrorCodes.InvalidOverride);

        var changes = proposedValues.Where(pair => current.TryGetValue(pair.Key, out var before) && !Equals(before, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var original = changes.Keys.ToDictionary(key => key, key => current[key], StringComparer.Ordinal);
        var hasBaseline = baseline is not null;
        var baselineFingerprint = hasBaseline ? FingerprintPreferences(baseline!) : string.Empty;
        if (hasBaseline && !string.Equals(baselineFingerprint, FingerprintPreferences(current), StringComparison.Ordinal))
            blocks.Add("BASELINE_STALE");
        if (changes.Count == 0) blocks.Add("NO_EFFECTIVE_CHANGES");

        return new(Guid.NewGuid(), targetSessionId, revision, createdUtc.ToUniversalTime(),
            FingerprintInputs(inputs), baselineFingerprint, !hasBaseline,
            hasBaseline && blocks.Count == 0, false, [.. blocks.OrderBy(code => code, StringComparer.Ordinal)],
            [.. recommendations], [.. omissions.OrderBy(o => o.ApiKey, StringComparer.Ordinal)],
            new ReadOnlyDictionary<string, PreferenceValue>(original), new ReadOnlyDictionary<string, PreferenceValue>(changes));
    }

    // Acceptance records review of this exact applicable snapshot; rebuilding always produces a fresh unapproved plan.
    public static Plan Approve(Plan plan, Guid planId, long revision)
    {
        if (plan is null || !plan.Applicable || plan.Id != planId || plan.Revision != revision || plan.Approved)
            throw new QbittorrentException(ErrorCodes.PlanStale);
        return plan with { Approved = true };
    }

    private static PreferenceValue ValidateOverride(string key, PreferenceValue value, bool vpnRequired, IReadOnlyList<TargetInterface> interfaces)
    {
        if ((key is "max_connec" or "max_connec_per_torrent" or "max_uploads" or "max_uploads_per_torrent")
            && value is IntegerPreference { Value: -1 }) return value;
        var range = Range(key);
        if (range is not null)
        {
            if (value is not IntegerPreference integer || integer.Value < range.Minimum || integer.Value > range.Maximum
                || ((long)integer.Value - (long)range.Minimum) % (long)range.Step != 0)
                throw new QbittorrentException(ErrorCodes.InvalidOverride);
            if (key == "send_buffer_low_watermark" || key == "send_buffer_watermark") return value;
            return value;
        }
        switch (key, value)
        {
            case ("random_port", BooleanPreference):
                if (((BooleanPreference)value).Value) throw new QbittorrentException(ErrorCodes.InvalidOverride);
                return value;
            case ("enable_coalesce_read_write" or "limit_utp_rate" or "queueing_enabled" or "preallocate_all"
                or "anonymous_mode" or "dht" or "pex" or "lsd", BooleanPreference):
                return value;
            case ("bittorrent_protocol", IntegerPreference { Value: >= 0 and <= 2 }):
            case ("encryption", IntegerPreference { Value: >= 0 and <= 2 }):
            case ("disk_io_read_mode", IntegerPreference { Value: >= 0 and <= 1 }):
            case ("disk_io_write_mode", IntegerPreference { Value: >= 0 and <= 2 }):
                return value;
            case ("current_network_interface", StringPreference text):
                var matches = interfaces.Where(i => string.Equals(i.Id, text.Value, StringComparison.Ordinal)
                        || string.Equals(i.Name, text.Value, StringComparison.Ordinal))
                    .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
                if (matches.Length != 1 || vpnRequired && string.IsNullOrWhiteSpace(matches[0].Id))
                    throw new QbittorrentException(ErrorCodes.InvalidOverride);
                return new StringPreference(matches[0].Id);
            default:
                throw new QbittorrentException(ErrorCodes.InvalidOverride);
        }
    }

    private static bool Editable(string key, PreferenceValue value) => value switch
    {
        BooleanPreference => true,
        StringPreference => key == "current_network_interface",
        IntegerPreference => Range(key) is not null || key is "bittorrent_protocol" or "encryption" or "disk_io_read_mode" or "disk_io_write_mode" or "random_port",
        _ => false
    };

    private static AllowedRange? Range(string key) => key switch
    {
        "up_limit" or "dl_limit" => new(0, (MaxInt / 1024) * 1024, 1024),
        "max_connec" or "max_connec_per_torrent" or "max_uploads" or "max_uploads_per_torrent" => new(1, MaxInt, 1),
        "max_active_downloads" or "max_active_uploads" or "max_active_torrents" => new(-1, MaxInt, 1),
        "async_io_threads" => new(1, 1024, 1),
        // ponytail: common 32-bit cache cap avoids needing remote process bitness; lift after buildInfo exposes it.
        "disk_cache" => new(-1, 1536, 1),
        "send_buffer_watermark" or "send_buffer_low_watermark" => new(0, MaxInt / 1024, 1),
        "send_buffer_watermark_factor" or "socket_backlog_size" or "connection_speed" => new(0, MaxInt, 1),
        "listen_port" => new(49152, 65535, 1),
        _ => null
    };

    private static AllowedValue[] AllowedValues(string key, IReadOnlyList<TargetInterface> interfaces) => key switch
    {
        "bittorrent_protocol" => new[] { 0, 1, 2 }.Select(v => new AllowedValue(new IntegerPreference(v), "settings.values." + key + "." + v)).ToArray(),
        "encryption" => new[] { 0, 1, 2 }.Select(v => new AllowedValue(new IntegerPreference(v), "settings.values." + key + "." + v)).ToArray(),
        "disk_io_read_mode" => new[] { 0, 1 }.Select(v => new AllowedValue(new IntegerPreference(v), "settings.values." + key + "." + v)).ToArray(),
        "disk_io_write_mode" => new[] { 0, 1, 2 }.Select(v => new AllowedValue(new IntegerPreference(v), "settings.values." + key + "." + v)).ToArray(),
        "random_port" => [new AllowedValue(new BooleanPreference(false), "settings.values.random_port.false")],
        "max_connec" or "max_connec_per_torrent" or "max_uploads" or "max_uploads_per_torrent" =>
            [new AllowedValue(new IntegerPreference(-1), "settings.values.unlimited")],
        "current_network_interface" => interfaces.Where(i => !string.IsNullOrWhiteSpace(i.Id)).GroupBy(i => i.Id, StringComparer.Ordinal)
            .Select(g => g.First()).Select(i => new AllowedValue(new StringPreference(i.Id), "settings.interfaces." + i.Id)).ToArray(),
        _ => []
    };

    private static RecommendationType ValueType(string key, PreferenceValue value) => key == "current_network_interface" ? RecommendationType.Interface
        : key is "bittorrent_protocol" or "encryption" or "disk_io_read_mode" or "disk_io_write_mode" ? RecommendationType.Enum
        : value is BooleanPreference ? RecommendationType.Bool : RecommendationType.Number;
    private static string Group(string key) => key is "listen_port" or "random_port" ? "port" : key;
    private static string ExplanationProperty(string key) => key switch
    {
        "up_limit" => "upload_limit",
        "dl_limit" => "download_limit",
        "max_connec" or "max_connec_per_torrent" => "max_connections",
        "max_uploads" or "max_uploads_per_torrent" => "upload_slots",
        "max_active_downloads" or "max_active_uploads" or "max_active_torrents" or "queueing_enabled" => "queue",
        "disk_cache" or "disk_io_read_mode" or "disk_io_write_mode" => "disk_cache",
        "async_io_threads" => "async_io",
        "enable_coalesce_read_write" => "coalesce",
        "bittorrent_protocol" => "protocol",
        "send_buffer_watermark" or "send_buffer_low_watermark" or "send_buffer_watermark_factor" => "send_buffer",
        "socket_backlog_size" => "socket_backlog",
        "listen_port" or "random_port" => "port",
        "encryption" => "encryption",
        "anonymous_mode" => "anonymous",
        "dht" or "pex" or "lsd" => "dht_pex_lsd",
        "current_network_interface" => "vpn_interface",
        "preallocate_all" => "pre_allocate",
        _ => string.Empty
    };
    private static string[] Cautions(string key, IReadOnlyList<CalculatorNotice> warnings)
    {
        var codes = key switch
        {
            "max_uploads" or "max_uploads_per_torrent" => new[] { "calculator.warning.privateTrackerUploadSlots" },
            "max_connec" or "max_connec_per_torrent" => new[] { "calculator.warning.seedboxMaxConnections" },
            "disk_cache" or "disk_io_read_mode" or "disk_io_write_mode" => new[] { "calculator.warning.nasOsCacheDisabled", "calculator.warning.trueNasCachePolicy" },
            "current_network_interface" => new[] { "calculator.warning.vpnInterfaceBound", "calculator.warning.vpnInterfaceMissing", "calculator.warning.dockerDefaultInterface" },
            "listen_port" => new[] { "calculator.warning.randomPort" },
            "encryption" => new[] { "calculator.warning.requireEncryption" },
            "anonymous_mode" => new[] { "calculator.warning.privateTrackerAnonymousMode" },
            "dht" or "pex" or "lsd" => new[] { "calculator.warning.privateTrackerDhtPexLsd" },
            _ => Array.Empty<string>()
        };
        return warnings.Where(warning => codes.Contains(warning.Code, StringComparer.Ordinal)).Select(warning => warning.Code).ToArray();
    }
    private static ResultCategory Category(string key) => key switch
    {
        "up_limit" or "dl_limit" or "send_buffer_watermark" or "send_buffer_low_watermark" or "send_buffer_watermark_factor" or "connection_speed" => ResultCategory.Throughput,
        "current_network_interface" or "bittorrent_protocol" or "encryption" or "anonymous_mode" or "dht" or "pex" or "lsd" => ResultCategory.Connectivity,
        "disk_cache" or "disk_io_read_mode" or "disk_io_write_mode" or "enable_coalesce_read_write" or "preallocate_all" or "async_io_threads" => ResultCategory.Resources,
        _ => ResultCategory.Stability
    };
    private static string Unit(string key) => key switch
    {
        "up_limit" or "dl_limit" => "bytesPerSecond",
        "disk_cache" => "mebibytes",
        "send_buffer_watermark" or "send_buffer_low_watermark" => "kibibytes",
        "send_buffer_watermark_factor" => "percent",
        "connection_speed" => "connectionsPerSecond",
        "current_network_interface" => "interface",
        "dht" or "pex" or "lsd" or "anonymous_mode" or "queueing_enabled" or "preallocate_all" or "random_port" or "enable_coalesce_read_write" or "limit_utp_rate" => "boolean",
        _ => "count"
    };
    public static string FingerprintInputs(DraftInputs inputs) => Hash(JsonSerializer.Serialize(inputs, Protocol.Json));
    public static string FingerprintPreferences(IReadOnlyDictionary<string, PreferenceValue> values)
    {
        var canonical = values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return Hash(JsonSerializer.Serialize(canonical, Protocol.Json));
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

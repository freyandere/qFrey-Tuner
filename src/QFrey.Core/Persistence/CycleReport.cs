using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QFrey.Core.Contracts;
using QFrey.Core.Localization;

namespace QFrey.Core.Persistence;

public sealed record CycleReportArtifact(string Extension, string ContentType, byte[] Bytes);

/// <summary>Bounded, pure renderers for validated persisted cycle records.</summary>
public static class CycleReport
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; form-action 'none'";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly IReadOnlyDictionary<string, (string Ru, string En)> Labels = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["title"] = ("Отчёт цикла", "Cycle report"), ["cycle"] = ("Цикл", "Cycle"),
        ["results.comparisonCaveat"] = ("Наблюдаемые изменения оцениваются с консервативным порогом шума. Отсчёты могут быть связаны; изменения пиров и кэша не позволяют утверждать причинную связь.",
            "Observed changes use a conservative noise threshold. Samples may be correlated; changing peers and cache state prevent a claim of causation."),
        ["target"] = ("Цель", "Target"), ["endpoint"] = ("Адрес API", "API endpoint"),
        ["qbittorrent"] = ("qBittorrent", "qBittorrent"), ["api"] = ("Web API", "Web API"),
        ["libtorrent"] = ("libtorrent", "libtorrent"), ["created"] = ("Создано", "Created"),
        ["updated"] = ("Обновлено", "Updated"), ["phase"] = ("Этап", "Stage"),
        ["applyStatus"] = ("Статус применения", "Apply status"), ["analysisVersion"] = ("Версия анализа", "Analysis version"),
        ["inputs"] = ("Входные данные", "Inputs"), ["network"] = ("Сеть", "Network"),
        ["hardware"] = ("Оборудование", "Hardware"), ["usage"] = ("Профиль использования", "Usage profile"),
        ["download"] = ("Скачивание", "Download"), ["upload"] = ("Отдача", "Upload"),
        ["connection"] = ("Тип соединения", "Connection type"), ["vpn"] = ("Использовать VPN", "Use VPN"),
        ["vpnInterface"] = ("Сетевой интерфейс VPN", "VPN interface"), ["throttling"] = ("Ограничение провайдера", "ISP throttling"),
        ["downloadSource"] = ("Источник скорости скачивания", "Download speed source"),
        ["uploadSource"] = ("Источник скорости отдачи", "Upload speed source"),
        ["hardwareSource"] = ("Источник данных об оборудовании", "Hardware data source"),
        ["storage"] = ("Накопитель", "Storage"), ["ram"] = ("Память", "Memory"),
        ["cpu"] = ("Ядра CPU", "CPU cores"), ["performanceCores"] = ("Производительные ядра", "Performance cores"),
        ["hybridCpu"] = ("Гибридная архитектура CPU", "Hybrid CPU"), ["tracker"] = ("Трекер", "Tracker"),
        ["role"] = ("Роль", "Role"), ["environment"] = ("Среда", "Environment"), ["proposedPort"] = ("Предлагаемый порт", "Proposed port"),
        ["preferences"] = ("Параметры клиента", "Client preferences"), ["original"] = ("Исходные", "Original"),
        ["intended"] = ("Заданные", "Intended"), ["readback"] = ("Прочитанные после записи", "Observed readback"),
        ["workload"] = ("Тестовая загрузка", "Test workload"), ["name"] = ("Имя", "Name"),
        ["size"] = ("Размер", "Size"), ["savePath"] = ("Папка на сервере", "Server save path"),
        ["hash"] = ("Хеш", "Hash"),
        ["catalogue"] = ("Образ каталога", "Catalogue image"), ["ownership"] = ("Владение подтверждено", "Ownership verified"),
        ["baseline"] = ("Исходный замер", "Baseline measurement"), ["after"] = ("Повторный замер", "After measurement"),
        ["status"] = ("Статус", "Status"), ["scope"] = ("Область", "Scope"), ["samples"] = ("Отсчёты", "Samples"),
        ["duration"] = ("Длительность", "Duration"), ["mean"] = ("Среднее", "Mean"), ["median"] = ("Медиана", "Median"),
        ["deviation"] = ("Стандартное отклонение", "Standard deviation"), ["zeroPercent"] = ("Нулевые отсчёты", "Zero samples"),
        ["results"] = ("Результаты", "Results"), ["evidence"] = ("Основание", "Evidence"), ["context"] = ("Контекст", "Context"),
        ["measuredAt"] = ("Время измерения", "Measured at"), ["reasonCodes"] = ("Коды причин", "Reason codes"),
        ["before"] = ("До", "Before"), ["delta"] = ("Изменение", "Change"), ["relativeDelta"] = ("Изменение, %", "Change, %"),
        ["verdict"] = ("Вывод", "Verdict"), ["observed"] = ("Наблюдаемое", "Observed"),
        ["intendedValue"] = ("Задано", "Intended"), ["backupAvailable"] = ("Резервная копия доступна", "Backup available"),
        ["conflicts"] = ("Конфликты", "Conflicts"), ["actions"] = ("Действия восстановления", "Recovery actions"),
        ["message"] = ("Сообщение", "Message"), ["messageParameters"] = ("Параметры сообщения", "Message parameters"),
        ["notTranslated"] = ("Непереведённые ключи сообщений", "Untranslated message keys"),
        ["historicalNote"] = ("Исторические данные не описывают текущие настройки.", "Historical data does not describe current settings."),
        ["comparisonNote"] = ("Наблюдаемое изменение не доказывает причинную связь.", "An observed change does not establish causation."),
        ["unknown"] = ("Неизвестно", "Unknown"), ["yes"] = ("Да", "Yes"), ["no"] = ("Нет", "No"),
        ["invalid"] = ("Недействительно", "Invalid"), ["notMeasured"] = ("Не измерено", "Not measured"),
        ["reasonFallback"] = ("Причина не распознана; сохранён стабильный код.", "Reason is not translated; the stable code is preserved."),
        ["scope.session"] = ("Вся сессия клиента", "Entire client session"),
        ["scope.workload"] = ("Выбранная загрузка", "Selected workload"),
        ["scope.localProcess"] = ("Локальный процесс", "Local process"),
        ["scope.localHost"] = ("Локальный компьютер", "Local host"),
        ["scope.endpoint"] = ("API-адрес", "API endpoint"),
        ["evidence.observed"] = ("Наблюдение", "Observed"), ["evidence.derived"] = ("Расчёт", "Derived"),
        ["evidence.heuristic"] = ("Эвристика", "Heuristic"), ["evidence.userProvided"] = ("Введено пользователем", "User provided"),
        ["verdict.observedImprovement"] = ("Наблюдаемое улучшение", "Observed improvement"),
        ["verdict.observedRegression"] = ("Наблюдаемое ухудшение", "Observed regression"),
        ["verdict.inconclusive"] = ("Убедительный вывод не получен", "Inconclusive"),
        ["verdict.notComparable"] = ("Несопоставимо", "Not comparable"), ["verdict.notMeasured"] = ("Не измерено", "Not measured"),
        ["unit.bytesPerSecond"] = ("МиБ/с", "MiB/s"), ["unit.bytes"] = ("байт", "bytes"),
        ["unit.milliseconds"] = ("мс", "ms"), ["unit.count"] = ("шт.", "count"), ["unit.percent"] = ("%", "%"),
        ["preferenceKey"] = ("Ключ", "Key"), ["preferenceValue"] = ("Значение", "Value"),
        ["connection.fiber"] = ("Оптоволокно", "Fiber"), ["connection.cableDsl"] = ("Кабель / DSL", "Cable / DSL"),
        ["connection.mobile4G"] = ("Мобильная сеть 4G", "Mobile 4G"),
        ["storage.hdd"] = ("Жёсткий диск (HDD)", "Hard disk (HDD)"), ["storage.ssdSata"] = ("SSD SATA", "SATA SSD"),
        ["storage.nvme"] = ("SSD NVMe", "NVMe SSD"),
        ["tracker.public"] = ("Открытый", "Public"), ["tracker.private"] = ("Закрытый", "Private"),
        ["role.leecher"] = ("Скачивает", "Downloader"), ["role.seeder"] = ("Раздаёт", "Seeder"),
        ["role.uploader"] = ("Загружает", "Uploader"),
        ["environment.system"] = ("Установленное приложение", "Installed application"),
        ["environment.portable"] = ("Портативная версия", "Portable"), ["environment.truenas"] = ("TrueNAS", "TrueNAS"),
        ["environment.nas"] = ("NAS", "NAS"), ["environment.docker"] = ("Docker", "Docker"),
        ["environment.seedbox"] = ("Seedbox", "Seedbox"),
        ["source.manual"] = ("Введено вручную", "Manual"), ["source.measured"] = ("Измерено", "Measured"),
        ["source.localDetection"] = ("Обнаружено локально", "Detected locally"),
        ["phase.draft"] = ("Черновик", "Draft"), ["phase.baselineReady"] = ("Исходный замер готов", "Baseline ready"),
        ["phase.planReady"] = ("План готов", "Plan ready"), ["phase.applying"] = ("Применение", "Applying"),
        ["phase.appliedVerified"] = ("Применение проверено", "Application verified"), ["phase.afterReady"] = ("Повторный замер готов", "After measurement ready"),
        ["phase.completed"] = ("Завершён", "Completed"), ["phase.recoveryRequired"] = ("Требуется восстановление", "Recovery required"),
        ["phase.rolledBack"] = ("Откат выполнен", "Rolled back"),
        ["apply.notApplied"] = ("Не применено", "Not applied"), ["apply.pending"] = ("Ожидает проверки", "Pending"),
        ["apply.verified"] = ("Проверено", "Verified"), ["apply.unverified"] = ("Не проверено", "Unverified"),
        ["apply.reverted"] = ("Откачено", "Reverted"),
        ["measurementStatus.pending"] = ("Ожидает", "Pending"), ["measurementStatus.warmingUp"] = ("Прогрев", "Warming up"),
        ["measurementStatus.sampling"] = ("Замер", "Sampling"), ["measurementStatus.valid"] = ("Действителен", "Valid"),
        ["measurementStatus.invalid"] = ("Недействителен", "Invalid"), ["measurementStatus.cancelled"] = ("Отменён", "Cancelled"),
        ["measurementKind.baseline"] = ("Исходный", "Baseline"), ["measurementKind.after"] = ("Повторный", "After"),
        ["resultContext.historical"] = ("Исторический", "Historical"), ["resultContext.currentExperiment"] = ("Текущий эксперимент", "Current experiment"),
        ["category.throughput"] = ("Скорость передачи", "Throughput"), ["category.stability"] = ("Стабильность", "Stability"),
        ["category.ramp"] = ("Разгон", "Ramp-up"), ["category.connectivity"] = ("Подключение", "Connectivity"),
        ["category.resources"] = ("Ресурсы", "Resources"), ["category.configuration"] = ("Настройки", "Configuration"),
        ["category.limitations"] = ("Ограничения", "Limitations"),
        ["metrics.download"] = ("Скачивание", "Download"), ["metrics.upload"] = ("Отдача", "Upload"),
        ["metrics.cpu"] = ("Ресурс CPU процесса", "Process CPU capacity"),
    };

    public static IReadOnlyList<string> GetUntranslatedMessageKeys(CycleRecord cycle, Locale locale = Locale.EnUs)
    {
        Validate(cycle);
        if (!Enum.IsDefined(locale)) throw new ArgumentOutOfRangeException(nameof(locale));
        return cycle.Experiment.Results
            .SelectMany(x => new[] { x.TitleKey, x.Explanation.Key })
            .Where(key => !HasTranslation(key, locale))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    private static bool HasTranslation(string key, Locale locale) => Labels.ContainsKey(key) || MessageCatalog.TryGet(key, locale, out _);

    public static CycleReportArtifact ToJson(CycleRecord cycle)
    {
        Validate(cycle);
        using var output = new BoundedMemoryStream(MaximumBytes);
        JsonSerializer.Serialize(output, cycle, JsonOptions);
        return new(".json", "application/json; charset=utf-8", output.ToArray());
    }

    public static CycleReportArtifact ToHtml(CycleRecord cycle, Locale locale)
    {
        Validate(cycle);
        if (!Enum.IsDefined(locale)) throw new ArgumentOutOfRangeException(nameof(locale));
        ValidateHtmlBound(cycle);
        var renderer = new HtmlRenderer(cycle, locale);
        var bytes = Encoding.UTF8.GetBytes(renderer.Render());
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("REPORT_TOO_LARGE");
        return new(".html", "text/html; charset=utf-8", bytes);
    }

    private static void ValidateHtmlBound(CycleRecord cycle)
    {
        long characters = 0;
        void Add(string? value)
        {
            characters += value?.Length ?? 0;
            if (characters > MaximumBytes / 12) throw new InvalidDataException("REPORT_TOO_LARGE");
        }
        void AddCodes(IEnumerable<string> codes) { foreach (var code in codes) Add(code); }
        Add(cycle.Target.Endpoint); Add(cycle.Target.QbittorrentVersion); Add(cycle.Target.ApiVersion); Add(cycle.Target.LibtorrentVersion);
        Add(cycle.AnalysisVersion); Add(cycle.OperationStage); Add(cycle.Inputs.Network.VpnInterface);
        foreach (var values in new[] { cycle.Original, cycle.IntendedApplied, cycle.ObservedReadback })
            foreach (var pair in values) { Add(pair.Key); if (pair.Value is StringPreference text) Add(text.Value); }
        if (cycle.Experiment.Workload is { } workload)
        {
            Add(workload.Name); Add(workload.TotalBytesDecimal); Add(workload.ServerSavePath); Add(workload.CatalogueId);
            foreach (var hash in workload.Reference.Hashes) Add(hash);
            AddCodes(workload.ReasonCodes);
        }
        foreach (var measurement in new[] { cycle.Experiment.Baseline, cycle.Experiment.After }.Where(x => x is not null))
        { Add(measurement!.AnalysisVersion); AddCodes(measurement.ReasonCodes); AddHistorical(measurement.MeanDownload); AddHistorical(measurement.MedianDownload); AddHistorical(measurement.StandardDeviation); AddHistorical(measurement.ZeroSamplePercent); }
        void AddHistorical(HistoricalValue value)
        {
            if (value is InvalidHistoricalValue invalid) AddCodes(invalid.ReasonCodes);
            if (value is NotMeasuredValue notMeasured) AddCodes(notMeasured.ReasonCodes);
        }
        foreach (var result in cycle.Experiment.Results)
        {
            Add(result.Id); Add(result.TitleKey); Add(result.Explanation.Key);
            foreach (var pair in result.Explanation.Parameters) { Add(pair.Key); if (pair.Value is TextParameter text) Add(text.Value); }
            switch (result.Content)
            {
                case ComparisonContent comparison:
                    Add(comparison.Comparison.MetricId); AddCodes(comparison.Comparison.ReasonCodes);
                    AddHistorical(comparison.Comparison.Before); AddHistorical(comparison.Comparison.After); break;
                case ObservationContent observation: Add(observation.MetricId); AddHistorical(observation.Value); break;
                case ChangeContent change: Add(change.ApiKey); AddPreference(change.Before); AddPreference(change.Intended); if (change.Observed is not null) AddPreference(change.Observed); break;
                case LimitationContent limitation: AddCodes(limitation.ReasonCodes); break;
                case RecoveryContent recovery: AddCodes(recovery.ConflictKeys); AddCodes(recovery.ActionIds); break;
            }
        }
        void AddPreference(PreferenceValue value) { if (value is StringPreference text) Add(text.Value); }
    }

    private static void Validate(CycleRecord cycle)
    {
        CycleStore.Validate(cycle);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            MaxDepth = 16,
            Encoder = JavaScriptEncoder.Default
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed class BoundedMemoryStream(int maximumLength) : MemoryStream
    {
        private void CheckWrite(int count)
        {
            if (count < 0 || Length + count > maximumLength) throw new InvalidDataException("REPORT_TOO_LARGE");
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); }
    }

    private sealed class HtmlRenderer(CycleRecord cycle, Locale locale)
    {
        private readonly CultureInfo culture = CultureInfo.GetCultureInfo(locale == Locale.RuRu ? "ru-RU" : "en-US");
        private readonly bool russian = locale == Locale.RuRu;
        private readonly StringBuilder html = new();

        public string Render()
        {
            html.Append("<!doctype html><html lang=\"").Append(russian ? "ru-RU" : "en-US")
                .Append("\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"")
                .Append(ContentSecurityPolicy).Append("\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
                .Append(E(T("title"))).Append("</title><style>")
                .Append("body{font:15px/1.5 Segoe UI,Arial,sans-serif;max-width:1000px;margin:2rem auto;padding:0 1rem;color:#172234;background:#f4f6fa}section{background:#fff;border:1px solid #cad3e0;border-radius:10px;padding:1rem;margin:1rem 0}h1,h2,h3{line-height:1.25}table{border-collapse:collapse;width:100%;margin:.5rem 0}th,td{text-align:left;vertical-align:top;border-bottom:1px solid #cad3e0;padding:.4rem}.code{font-family:Consolas,monospace;overflow-wrap:anywhere}.muted{color:#526079}.unknown{color:#805000}dl{display:grid;grid-template-columns:minmax(10rem,1fr) 2fr;gap:.25rem .75rem}dt{font-weight:600}dd{margin:0;overflow-wrap:anywhere}ul{padding-left:1.5rem}footer{margin:2rem 0;color:#526079;font-size:.9rem}</style></head><body>")
                .Append("<h1>").Append(E(T("title"))).Append("</h1><p class=\"muted\">").Append(E(T("historicalNote"))).Append("</p>");
            RenderCycle();
            RenderInputs();
            RenderPreferences();
            RenderWorkload();
            RenderMeasurement("baseline", cycle.Experiment.Baseline);
            RenderMeasurement("after", cycle.Experiment.After);
            RenderResults();
            RenderLocalizationGaps();
            html.Append("<footer>").Append(E(T("comparisonNote"))).Append("</footer></body></html>");
            return html.ToString();
        }

        private void RenderCycle()
        {
            StartSection("cycle", "<dl>");
            Row("cycle", cycle.CycleId);
            Row("endpoint", cycle.Target.Endpoint);
            Row("qbittorrent", cycle.Target.QbittorrentVersion);
            Row("api", cycle.Target.ApiVersion);
            Row("libtorrent", cycle.Target.LibtorrentVersion);
            Row("created", Date(cycle.CreatedUtc));
            Row("updated", Date(cycle.UpdatedUtc));
            var phaseName = char.ToUpperInvariant(cycle.OperationStage[0]) + cycle.OperationStage[1..];
            if (Enum.TryParse<ExperimentPhase>(phaseName, out var phase)) Row("phase", EnumText(phase));
            Row("applyStatus", EnumText(cycle.ApplyStatus));
            Row("analysisVersion", cycle.AnalysisVersion);
            html.Append("</dl></section>");
        }

        private void RenderInputs()
        {
            StartSection("inputs", "<h3>");
            html.Append(E(T("network"))).Append("</h3><dl>");
            Row("download", Number(cycle.Inputs.Network.DownloadMbps) + (russian ? " Мбит/с" : " Mbps"));
            Row("upload", Number(cycle.Inputs.Network.UploadMbps) + (russian ? " Мбит/с" : " Mbps"));
            Row("connection", EnumText(cycle.Inputs.Network.ConnectionType));
            Row("downloadSource", EnumText(cycle.Inputs.Network.DownloadSource));
            Row("uploadSource", EnumText(cycle.Inputs.Network.UploadSource));
            Row("vpn", YesNo(cycle.Inputs.Network.UseVpn));
            Row("vpnInterface", cycle.Inputs.Network.VpnInterface);
            Row("throttling", YesNo(cycle.Inputs.Network.IspThrottling));
            html.Append("</dl><h3>").Append(E(T("hardware"))).Append("</h3><dl>");
            Row("storage", EnumText(cycle.Inputs.Hardware.StorageType));
            Row("ram", cycle.Inputs.Hardware.RamGiB + (russian ? " ГиБ" : " GiB"));
            Row("cpu", cycle.Inputs.Hardware.CpuCores);
            Row("hybridCpu", YesNo(cycle.Inputs.Hardware.IsHybridCpu));
            Row("performanceCores", cycle.Inputs.Hardware.PerformanceCores);
            Row("hardwareSource", EnumText(cycle.Inputs.Hardware.Source));
            html.Append("</dl><h3>").Append(E(T("usage"))).Append("</h3><dl>");
            Row("tracker", EnumText(cycle.Inputs.Usage.TrackerType));
            Row("role", EnumText(cycle.Inputs.Usage.UserRole));
            Row("environment", EnumText(cycle.Inputs.Usage.Environment));
            if (cycle.Inputs.ProposedPort is { } port) Row("proposedPort", port);
            html.Append("</dl></section>");
        }

        private void RenderPreferences()
        {
            StartSection("preferences", "");
            PreferenceTable("original", cycle.Original);
            PreferenceTable("intended", cycle.IntendedApplied);
            PreferenceTable("readback", cycle.ObservedReadback);
            html.Append("</section>");
        }

        private void PreferenceTable(string title, IReadOnlyDictionary<string, PreferenceValue> values)
        {
            html.Append("<h3>").Append(E(T(title))).Append("</h3>");
            if (values.Count == 0) { html.Append("<p>").Append(E(T("unknown"))).Append("</p>"); return; }
            html.Append("<table><thead><tr><th>").Append(E(T("preferenceKey"))).Append("</th><th>").Append(E(T("preferenceValue"))).Append("</th></tr></thead><tbody>");
            foreach (var pair in values.OrderBy(x => x.Key, StringComparer.Ordinal))
                html.Append("<tr><td class=\"code\">").Append(E(pair.Key)).Append("</td><td>").Append(E(Preference(pair.Value))).Append("</td></tr>");
            html.Append("</tbody></table>");
        }

        private void RenderWorkload()
        {
            var workload = cycle.Experiment.Workload;
            if (workload is null) return;
            StartSection("workload", "<dl>");
            Row("name", workload.Name);
            Row("size", workload.TotalBytesDecimal + (russian ? " байт" : " bytes"));
            Row("savePath", workload.ServerSavePath);
            Row("catalogue", workload.CatalogueId ?? T("unknown"));
            Row("ownership", YesNo(workload.OwnershipVerified));
            Row("hash", string.Join(", ", workload.Reference.Hashes));
            if (workload.ReasonCodes.Length > 0) Row("reasonCodes", Codes(workload.ReasonCodes));
            html.Append("</dl></section>");
        }

        private void RenderMeasurement(string key, MeasurementSummary? measurement)
        {
            if (measurement is null) return;
            StartSection(key, "<dl>");
            Row("status", EnumText(measurement.Status));
            Row("scope", Scope(measurement.Scope));
            Row("samples", measurement.SampleCount);
            Row("duration", Number(measurement.DurationMs) + " " + T("unit.milliseconds"));
            Row("mean", Historical(measurement.MeanDownload, MetricUnit.BytesPerSecond));
            Row("median", Historical(measurement.MedianDownload, MetricUnit.BytesPerSecond));
            Row("deviation", Historical(measurement.StandardDeviation, MetricUnit.BytesPerSecond));
            Row("zeroPercent", Historical(measurement.ZeroSamplePercent, MetricUnit.Percent));
            Row("analysisVersion", measurement.AnalysisVersion);
            if (measurement.ReasonCodes.Length > 0) Row("reasonCodes", Codes(measurement.ReasonCodes));
            html.Append("</dl></section>");
        }

        private void RenderResults()
        {
            if (cycle.Experiment.Results.Length == 0) return;
            StartSection("results", "");
            foreach (var result in cycle.Experiment.Results)
            {
                html.Append("<article><h3>").Append(E(Message(result.TitleKey))).Append("</h3><dl>");
                Row("category." + Camel(result.Category), EnumText(result.Category));
                Row("evidence", Evidence(result.Evidence));
                Row("context", EnumText(result.Context));
                Row("message", Message(result.Explanation.Key, result.Explanation.Parameters));
                if (result.MeasuredAtUtc is { } at) Row("measuredAt", Date(at));
                html.Append("</dl>");
                RenderContent(result.Content);
                html.Append("</article>");
            }
            html.Append("</section>");
        }

        private void RenderContent(ResultContent content)
        {
            switch (content)
            {
                case ComparisonContent comparison:
                    RenderComparison(comparison.Comparison);
                    break;
                case ObservationContent observation:
                    html.Append("<dl>");
                    Row("name", observation.MetricId);
                    Row("scope", Scope(observation.Scope));
                    Row("status", Historical(observation.Value, observation.Unit));
                    html.Append("</dl>");
                    break;
                case ChangeContent change:
                    html.Append("<dl>");
                    Row("name", change.ApiKey);
                    Row("before", Preference(change.Before));
                    Row("intendedValue", Preference(change.Intended));
                    Row("observed", change.Observed is null ? T("unknown") : Preference(change.Observed));
                    Row("status", EnumText(change.Status));
                    html.Append("</dl>");
                    break;
                case LimitationContent limitation:
                    Row("reasonCodes", Codes(limitation.ReasonCodes));
                    break;
                case RecoveryContent recovery:
                    html.Append("<dl>");
                    Row("conflicts", Codes(recovery.ConflictKeys));
                    Row("actions", Codes(recovery.ActionIds));
                    Row("backupAvailable", YesNo(recovery.BackupAvailable));
                    html.Append("</dl>");
                    break;
            }
        }

        private void RenderComparison(ComparisonValue comparison)
        {
            html.Append("<dl>");
            Row("scope", Scope(comparison.Scope));
            Row("before", Historical(comparison.Before, comparison.Unit));
            Row("after", Historical(comparison.After, comparison.Unit));
            Row("delta", comparison.AbsoluteDelta is { } delta ? FormatUnit(delta, comparison.Unit) : T("notMeasured"));
            Row("relativeDelta", comparison.RelativeDeltaPercent is { } relative ? Number(relative) + "%" : T("notMeasured"));
            Row("verdict", Verdict(comparison.Verdict));
            Row("evidence", Evidence(comparison.Evidence));
            if (comparison.ReasonCodes.Length > 0) Row("reasonCodes", Codes(comparison.ReasonCodes));
            html.Append("</dl>");
        }

        private void RenderLocalizationGaps()
        {
            var gaps = cycle.Experiment.Results.SelectMany(x => new[] { x.TitleKey, x.Explanation.Key })
                .Where(key => !HasTranslation(key, locale)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (gaps.Length == 0) return;
            StartSection("notTranslated", "<ul class=\"unknown\">");
            foreach (var gap in gaps) html.Append("<li class=\"code\">").Append(E(gap)).Append("</li>");
            html.Append("</ul></section>");
        }

        private void Row(string key, object? value) => html.Append("<dt>").Append(E(T(key))).Append("</dt><dd>").Append(E(value?.ToString() ?? T("unknown"))).Append("</dd>");
        private void StartSection(string titleKey, string inner) => html.Append("<section><h2>").Append(E(T(titleKey))).Append("</h2>").Append(inner);
        private string T(string key) => Labels.TryGetValue(key, out var pair) ? russian ? pair.Ru : pair.En : key;
        private string Message(string key, IReadOnlyDictionary<string, MessageParameter>? parameters = null)
        {
            var template = Labels.TryGetValue(key, out var pair)
                ? russian ? pair.Ru : pair.En
                : MessageCatalog.TryGet(key, locale, out var translated)
                    ? translated
                    : (russian ? "Непереведённое сообщение · ключ: " : "Untranslated message · key: ") + key;
            if (parameters is null || parameters.Count == 0) return template;
            return Regex.Replace(template, @"\{([A-Za-z0-9_]+)\}", match =>
            {
                var name = match.Groups[1].Value;
                return parameters.TryGetValue(name, out var value)
                    ? MessageCatalog.LocalizeParameter(name, Parameter(value), locale)
                    : T("unknown");
            });
        }
        private string YesNo(bool value) => T(value ? "yes" : "no");
        private string Number(double value) => value.ToString("N2", culture);
        private string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        private string Preference(PreferenceValue value) => value switch
        {
            IntegerPreference integer => integer.Value.ToString(culture),
            BooleanPreference boolean => YesNo(boolean.Value),
            StringPreference text => text.Value,
            _ => T("unknown")
        };
        private string Parameter(MessageParameter value) => value switch
        {
            NumberParameter number => number.Value.ToString("N2", culture),
            TextParameter text => text.Value,
            _ => T("unknown")
        };
        private string Historical(HistoricalValue value, MetricUnit unit) => value switch
        {
            ValidHistoricalValue valid => FormatUnit(valid.Value, unit) + " · " + Date(valid.MeasuredAtUtc),
            InvalidHistoricalValue invalid => T("invalid") + ": " + Codes(invalid.ReasonCodes),
            NotMeasuredValue missing => T("notMeasured") + ": " + Codes(missing.ReasonCodes),
            _ => T("unknown")
        };
        private string FormatUnit(double value, MetricUnit unit) => unit switch
        {
            MetricUnit.BytesPerSecond => Number(value / (1024d * 1024d)) + " " + T("unit.bytesPerSecond"),
            MetricUnit.Bytes => Number(value) + " " + T("unit.bytes"),
            MetricUnit.Milliseconds => Number(value) + " " + T("unit.milliseconds"),
            MetricUnit.Count => Number(value) + " " + T("unit.count"),
            MetricUnit.Percent => Number(value) + " " + T("unit.percent"),
            _ => Number(value)
        };
        private string Scope(MetricScope scope) => T("scope." + Camel(scope));
        private string Evidence(Evidence evidence) => T("evidence." + Camel(evidence));
        private string Verdict(Verdict verdict) => T("verdict." + Camel(verdict));
        private string Codes(IEnumerable<string> codes) => string.Join(", ", codes.Select(code =>
            MessageCatalog.TryGet("results.reason." + code, locale, out var message)
                ? message + " [" + code + "]"
                : T("reasonFallback") + " [" + code + "]"));
        private string EnumText<TEnum>(TEnum value) where TEnum : struct, Enum
        {
            var key = value switch
            {
                ApplyStatus item => "apply." + Camel(item),
                ExperimentPhase item => "phase." + Camel(item),
                MeasurementStatus item => "measurementStatus." + Camel(item),
                MeasurementKind item => "measurementKind." + Camel(item),
                ConnectionType item => "connection." + Camel(item),
                ResultCategory item => "category." + Camel(item),
                StorageType item => "storage." + Camel(item),
                TrackerType item => "tracker." + Camel(item),
                UserRole item => "role." + Camel(item),
                EnvironmentProfile item => "environment." + Camel(item),
                InputSource item => "source." + Camel(item),
                ResultContext item => "resultContext." + Camel(item),
                _ => Camel(value)
            };
            return T(key);
        }
        private static string Camel<T>(T value) where T : struct, Enum => char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
        private static string E(string value) => WebUtility.HtmlEncode(value);
    }
}

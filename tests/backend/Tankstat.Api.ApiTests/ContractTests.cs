using System.Net.Http.Json;
using System.Text.Json;

namespace Tankstat.Api.ApiTests;

/// <summary>
/// Black-box contract tests against a running server (TANKSTAT_API_URL, default http://localhost:5080).
/// They pin the GraphQL schema shape the frontend depends on. Run via `mise run test:api`.
/// </summary>
public class ContractTests
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("TANKSTAT_API_URL") ?? "http://localhost:5080"),
    };

    private static async Task<JsonElement> Query(string query)
    {
        var response = await Http.PostAsJsonAsync("/graphql", new { query });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task PhotoReading_IsOffUnlessSetUp_AndUnknownDraftsAreLeftOut()
    {
        var body = await Query("{ recognitionStatus { available } photoDrafts(ids: [\"00000000-0000-0000-0000-000000000001\"]) { id } }");

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        var data = body.GetProperty("data");
        Assert.False(data.GetProperty("recognitionStatus").GetProperty("available").GetBoolean());
        Assert.Empty(data.GetProperty("photoDrafts").EnumerateArray());
    }

    [Fact]
    public async Task PasswordRequirements_ArePublic_AndStartAtTheFloor()
    {
        var body = await Query("{ passwordRequirements { minLength maxLength } }");

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        var requirements = body.GetProperty("data").GetProperty("passwordRequirements");
        Assert.Equal((10, 128), (requirements.GetProperty("minLength").GetInt32(), requirements.GetProperty("maxLength").GetInt32()));
    }

    [Fact]
    public async Task Health_ExposesContractFields()
    {
        var body = await Query("{ health { status version uptime databaseReachable } }");

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        var health = body.GetProperty("data").GetProperty("health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, health.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.String, health.GetProperty("uptime").ValueKind);
        Assert.Equal(JsonValueKind.True, health.GetProperty("databaseReachable").ValueKind);
    }

    private static async Task<List<string?>> FieldNames(string type)
    {
        var body = await Query($"{{ __type(name: \"{type}\") {{ fields {{ name }} }} }}");
        return body.GetProperty("data").GetProperty("__type").GetProperty("fields")
            .EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
    }

    [Theory]
    [InlineData("Query", "health", "vehicles", "myVehicles", "myVehicleCount", "vehicle", "session", "notices", "users", "canSetUserPasswords", "accessSettings", "accessGrants", "trash", "vehicleCount", "trashCount", "trashDeletableCount", "vehicleDefaults", "refuelings", "refuelingCount", "refueling", "logDefaults", "refuelingTrash", "refuelingTrashCount", "refuelingTrashDeletableCount", "vehicleLogAccess", "shareCandidates", "notifications", "notificationCount", "recognitionStatus", "photoDrafts", "uiSettings", "mySessions", "offlineChanges", "offlineSettings", "parkedChanges", "systemSettings", "backups", "backupDetails", "setup", "setupRestorePoints", "setupBackupDetails", "setupUploadLimitMb", "passwordRequirements", "vehicleTrend", "vehicleFigure", "vehicleWidgets")]
    [InlineData("Mutation", "addVehicle", "logRefueling", "login", "logout", "changePassword", "requestPasswordReset", "resetPassword", "createUser", "issuePasswordReset", "setUserAdmin", "setUserDisabled", "updateUser", "setUserPassword", "deleteUser", "setDefaultAccess", "setAccessGrant", "updateVehicle", "deleteVehicle", "restoreVehicle", "emptyTrash", "logRefueling", "updateRefueling", "deleteRefueling", "restoreRefueling", "emptyRefuelingTrash", "setVehicleLogAccess", "markNotificationsRead", "updateUiSettings", "saveGridSettings", "resetGridSettings", "setVehicleOrder", "addRecurringExpense", "updateRecurringExpense", "deleteRecurringExpense", "markRecurringExpensesDone", "saveDashboardLayout", "resetDashboardLayout", "saveVehicleWidget", "revokeSession", "revokeOtherSessions", "updateOfflineSettings", "syncChanges", "resolveSyncChange", "updateSystemSettings", "restartServer", "restoreBackup", "runBackup", "deleteBackup", "holdBackup", "prepareBackupEncryption", "setupClaimAdministrator", "setupFinish")]
    [InlineData("Session", "mode", "user", "instance")]
    [InlineData("InstanceInfo", "generation", "restorePoint")]
    [InlineData("BackupOverviewInfo", "folder", "migration", "restorePoints", "sizes", "run", "settings")]
    [InlineData("RestorePointInfo", "file", "bytes", "createdAt", "appVersion", "provider", "migration", "sameMigration", "olderMigration", "safetyNet", "uploaded", "encrypted", "recipient", "currentRecipient", "photos", "keys", "tables", "rows", "settingsFile", "environment", "held", "heldSince")]
    [InlineData("BackupDetailsInfo", "file", "bytes", "held", "heldSince", "format", "writtenBy", "createdAt", "appVersion", "provider", "migration", "safetyNet", "uploaded", "encrypted", "recipient", "currentRecipient", "encryptionMethod", "pictures", "photos", "keys", "tables", "sizes", "archiveEntry", "sha256", "settingsFile", "environment")]
    [InlineData("BackupTableInfo", "name", "rows")]
    [InlineData("BackupSizesInfo", "tables", "pictures", "photos", "keys", "archive")]
    [InlineData("BackupSettingsFileInfo", "state", "omitted", "secrets", "secretsLeftOut", "machineLeftOut", "bytes")]
    [InlineData("BackupEnvironmentInfo", "mode", "settings", "server", "missing", "missingCertain", "overridden", "logLevelsMissing")]
    [InlineData("StorageSizesInfo", "database", "pictures", "photos")]
    [InlineData("BackupRunInfo", "running", "startedAt", "finishedAt", "file", "bytes", "error")]
    [InlineData("ServerFolder", "kind", "path", "setting", "variable", "sameAsPictures")]
    [InlineData("BackupSettingsInfo", "recipient", "photosApart", "photos", "maxUploadMb", "times", "days", "timeZone", "nextRun", "keep", "keepDays", "environment", "secrets", "passphraseMinLength")]
    [InlineData("BackupKeySetInfo", "passphrase", "generated", "identity", "recipient", "salt")]
    [InlineData("UserInfo", "id", "displayName", "email", "isAdmin", "passwordChangeRequired")]
    [InlineData("UserSessionInfo", "id", "client", "createdAt", "lastUsedAt", "expiresAt", "current")]
    [InlineData("UserAccount", "id", "provider", "email", "displayName", "isAdmin", "isDisabled")]
    [InlineData("PasswordResetLink", "token", "url", "emailSent", "emailFailed")]
    [InlineData("Notice", "code", "severity", "message")]
    [InlineData("SystemSettingsInfo", "file", "groups", "restartPending", "folders")]
    [InlineData("SystemSettingsGroup", "name", "settings", "appliesAtOnce")]
    [InlineData("SystemSetting", "key", "kind", "choices", "value", "isSet", "isSecret", "source", "editable", "restartPending", "defaultValue", "appliesAtOnce")]
    [InlineData("PasswordRequirementsInfo", "minLength", "maxLength")]
    [InlineData("SettingsFileInfo", "path", "exists", "ignored", "problems")]
    [InlineData("Vehicle", "id", "ownerId", "name", "licensePlate", "fuelType", "units", "deletedAt", "canEdit", "logAccess", "owner", "refuelingCount", "version", "updatedAt", "logCountSince", "parkedChangeCount", "changedAt", "changedBy", "lastChange", "summary", "dashboard")]
    [InlineData("MeasurementUnits", "distance", "volume")]
    [InlineData("UserRef", "id", "displayName", "avatarUrl")]
    [InlineData("LogDefaults", "lastOdometer", "lastDate", "currency")]
    [InlineData("VehicleDefaults", "distanceUnit", "volumeUnit", "currency", "recurringWarnDays", "recurringWarnDistance")]
    [InlineData("LogAccessGrantInfo", "user", "level")]
    [InlineData("Expense", "id", "vehicleId", "createdBy", "date", "title", "category", "amount", "currency", "odometer", "note", "deletedAt", "canEdit", "canDelete", "vehicle", "photos", "schedules", "version", "updatedAt", "changedAt", "changedBy", "lastChange")]
    [InlineData("MarkRecurringExpensesDonePayload", "schedules", "expense")]
    [InlineData("RecurringExpenseInfo", "id", "vehicleId", "title", "createdAt", "status", "version", "updatedAt", "changedAt", "changedBy", "lastChange")]
    [InlineData("RecurringRef", "id", "title")]
    [InlineData("OfflineChanges", "vehicle", "refuelings", "expenses", "recurring", "removed", "next", "watermark", "resync", "tyres")]
    [InlineData("VehicleTyres", "vehicleId", "sets", "days", "readings", "hasReadings")]
    [InlineData("OdometerPoint", "date", "value")]
    [InlineData("TyreSetInfo", "set", "figures", "stretches", "onTheCar")]
    [InlineData("TyreSet", "id", "vehicleId", "name", "season", "size", "since", "madeWeek", "madeYear", "roughDistance", "roughUntil", "retiredOn", "isRetired", "deletedAt", "version", "updatedAt", "changedAt", "changedBy", "lastChange", "createdBy")]
    [InlineData("TyreSetFigures", "distance", "approximate", "measured", "pairOneFront", "pairOneRear", "pairsUnknown", "suggestion")]
    [InlineData("RoughSuggestion", "distance", "until", "measuredBefore")]
    [InlineData("TyreStretchInfo", "stretch", "distance")]
    [InlineData("TyreStretch", "id", "vehicleId", "setId", "from", "until", "pairOneFront", "isOpen", "deletedAt", "version", "updatedAt", "changedAt", "lastChange")]
    [InlineData("StretchDistance", "distance", "approximate", "measure")]
    [InlineData("TyreLogDay", "id", "vehicleId", "day", "odometer", "note", "version", "updatedAt")]
    [InlineData("TyreTrashRow", "kind", "id", "vehicleId", "setName", "from", "until", "stretchCount", "deletedAt", "version", "vehicle", "canDelete")]
    [InlineData("RemovedEntity", "type", "id")]
    [InlineData("OfflineSettingsInfo", "defaultWindow", "vehicles")]
    [InlineData("OfflineVehicleWindow", "vehicleId", "window")]
    [InlineData("LogCountSince", "refuelings", "expenses")]
    [InlineData("SyncResultInfo", "results", "applied", "parked")]
    [InlineData("SyncChangeResultInfo", "id", "status", "entityId", "version", "reason")]
    [InlineData("SyncReasonInfo", "key", "args")]
    [InlineData("SyncReasonArg", "name", "value")]
    [InlineData("SyncChangeInfo", "id", "kind", "status", "vehicleId", "targetId", "expectedVersion", "change", "receivedAt", "reason", "submittedBy", "canResolve", "base", "current")]
    [InlineData("SyncCurrent", "state", "version", "changedAt", "changedBy", "lastChange", "vehicle", "refueling", "expense", "schedule", "tyreSet", "tyreStretch")]
    [InlineData("LogPhotoInfo", "id", "url", "reading")]
    [InlineData("RecognitionStatusInfo", "available")]
    [InlineData("PhotoDraftInfo", "id", "url", "reading")]
    [InlineData("PhotoReadingInfo", "status", "kind", "values")]
    [InlineData("ReadingValueInfo", "name", "value", "confidence")]
    [InlineData("NotificationInfo", "id", "kind", "createdAt", "updatedAt", "read", "readAt", "count", "subject", "context", "args")]
    [InlineData("NotificationRefInfo", "type", "id")]
    [InlineData("NotificationArg", "name", "value")]
    [InlineData("ImportPreviewInfo", "sourceVehicle", "fuelRows", "expenseRows", "recurringRows", "duplicateFuelRows", "duplicateExpenseRows", "duplicateRecurringRows", "firstDate", "lastDate", "categories", "issues")]
    [InlineData("ImportResultInfo", "vehicleId", "fuelImported", "expensesImported", "recurringImported", "fuelSkippedDuplicates", "expensesSkippedDuplicates", "recurringSkippedDuplicates", "errors")]
    [InlineData("ImportIssueInfo", "section", "row", "key", "args")]
    [InlineData("VehicleSummary", "lastFillUpDate", "latestOdometer", "averageConsumption", "currency", "thisMonthSpend", "lastMonthSpend", "spendTrend", "fillUpCount", "expenseCount", "spending", "trends")]
    [InlineData("DashboardTrends", "spending", "costPerDistance", "consumption", "distance")]
    [InlineData("CurrencyChange", "currency", "recent", "earlier")]
    [InlineData("ValueChange", "recent", "earlier")]
    [InlineData("ConsumptionChange", "recent", "earlier", "recentFillUps", "earlierFillUps")]
    [InlineData("CurrencySpend", "currency", "thisMonth", "lastMonth")]
    [InlineData("ChartData", "unit", "series")]
    [InlineData("ChartSeries", "kind", "currency", "points", "role", "slope", "metric", "unit")]
    [InlineData("DashboardInfo", "shared", "personal", "canChangeShared")]
    [InlineData("DashboardLayoutInfo", "tiles", "hidden", "updatedAt")]
    [InlineData("DashboardTileInfo", "id", "x", "y", "w", "h", "pinned", "period", "recipe")]
    [InlineData("TileRecipe", "widget", "title", "chart", "figure", "template")]
    [InlineData("FigureRecipe", "measure", "period", "sparkline", "template")]
    [InlineData("ChartConfig", "metric", "grouping", "kind", "range", "stacked", "from", "to", "trend", "runningTotal", "compareYearEarlier", "secondMetric", "secondKind")]
    [InlineData("SavedWidget", "id", "title", "widget", "chart", "figure", "isShared", "createdAt", "canEdit", "canDelete", "createdBy")]
    [InlineData("TrendPeriod", "kind", "unit", "count", "since", "whole", "against")]
    [InlineData("TrendFigureData", "recent", "earlier", "currencies", "consumption", "distance")]
    [InlineData("TrendWindow", "start", "end", "fillUps")]
    [InlineData("FigureData", "recent", "earlier", "unit", "currencies", "value", "lastFillUp")]
    [InlineData("LastFillUpFigure", "date", "fillUps", "expenses")]
    [InlineData("VehicleChart", "id", "vehicleId", "title", "metric", "grouping", "kind", "range", "rangeFrom", "rangeTo", "stacked", "trend", "runningTotal", "compareYearEarlier", "secondMetric", "secondKind", "isShared", "createdAt", "canEdit", "canDelete", "createdBy")]
    [InlineData("Refueling", "id", "vehicleId", "createdBy", "date", "volume", "totalCost", "discount", "currency", "odometer", "pricePerUnit", "pricePerUnitBeforeDiscount", "consumption", "isFullTank", "missedPreviousFillUp", "note", "deletedAt", "canEdit", "canDelete", "vehicle", "photos", "reviewState", "filledFromPhoto", "version", "updatedAt", "changedAt", "changedBy", "lastChange")]
    [InlineData("Expense", "id", "amount", "discount", "currency", "odometer", "reviewState", "filledFromPhoto")]
    [InlineData("UiSettingsInfo", "navOpen", "language", "colorMode", "surface", "timeFormat", "dateFormat", "defaultTimeFormat", "defaultDateFormat", "grids")]
    [InlineData("GridSettingsInfo", "gridId", "order", "hidden", "pageSize", "sortColumn", "sortDirection")]
    public async Task Schema_TypeExposesContractFields(string type, params string[] fields)
    {
        var names = await FieldNames(type);

        foreach (var field in fields) Assert.Contains(field, names);
    }

    [Theory]
    [InlineData("LogRefuelingInput", "vehicleId", "date", "volume", "totalCost", "discount", "currency", "odometer", "isFullTank", "note", "photoIds", "missedPreviousFillUp", "id")]
    [InlineData("UpdateRefuelingInput", "id", "date", "volume", "totalCost", "discount", "currency", "odometer", "isFullTank", "note", "missedPreviousFillUp")]
    [InlineData("AddExpenseInput", "vehicleId", "date", "title", "category", "amount", "discount", "currency", "odometer", "note", "photoIds", "id")]
    [InlineData("UpdateExpenseInput", "id", "date", "title", "category", "amount", "discount", "currency", "odometer", "note")]
    [InlineData("AddVehicleInput", "name", "licensePlate", "fuelType", "units", "id")]
    [InlineData("AddRecurringExpenseInput", "vehicleId", "title", "kind", "intervalMonths", "intervalDistance", "lastDoneDate", "lastDoneOdometer", "id")]
    [InlineData("MarkRecurringExpensesDoneInput", "ids", "date", "odometer", "amount", "discount", "currency", "title", "category", "photoIds", "expenseId")]
    [InlineData("GridSettingsInput", "gridId", "order", "hidden", "pageSize", "sortColumn", "sortDirection")]
    [InlineData("UpdateUiSettingsInput", "navOpen", "language", "clearLanguage", "colorMode", "surface", "timeFormat", "clearTimeFormat", "dateFormat", "clearDateFormat")]
    [InlineData("ChangeInput", "id", "expectedVersion", "base", "setVehiclePicture", "removeVehiclePicture", "addTyreSet", "updateTyreSet", "setTyreRoughDistance",
        "retireTyreSet", "unretireTyreSet", "deleteTyreSet", "restoreTyreSet", "swapTyres", "changeTyrePositions", "addPastTyreSwaps", "addTyreStretch",
        "updateTyreStretch", "deleteTyreStretch", "restoreTyreStretch", "setTyreLogDay")]
    [InlineData("AddTyreSetInput", "vehicleId", "name", "season", "size", "since", "madeWeek", "madeYear", "fitted", "day", "roughDistance", "odometer", "pairOneFront", "replace", "id", "stretchId")]
    [InlineData("SwapTyresInput", "vehicleId", "setId", "day", "pairOneFront", "replace", "odometer", "note", "stretchId")]
    [InlineData("AddPastTyreSwapsInput", "vehicleId", "swaps", "replace")]
    [InlineData("PastTyreSwapInput", "day", "setId", "odometer", "stretchId")]
    [InlineData("AddTyreStretchInput", "setId", "from", "until", "pairOneFront", "odometerFrom", "odometerUntil", "id")]
    [InlineData("SetTyreLogDayInput", "vehicleId", "day", "odometer", "note", "id")]
    [InlineData("SetVehiclePictureInput", "vehicleId", "draftId")]
    [InlineData("ResolveSyncChangeInput", "id", "action", "change", "restoreFirst")]
    [InlineData("SystemSettingChangeInput", "key", "value", "clear")]
    [InlineData("ChangePasswordInput", "currentPassword", "newPassword", "newPasswordConfirmation")]
    [InlineData("ResetPasswordInput", "token", "newPassword", "newPasswordConfirmation")]
    [InlineData("SetupFinishInput", "code", "email", "password", "passwordConfirmation", "acknowledgeOpenAccess")]
    [InlineData("SaveDashboardLayoutInput", "vehicleId", "personal", "tiles", "hidden", "layoutVersion")]
    [InlineData("DashboardTileInput", "id", "x", "y", "w", "h", "pinned", "period", "recipe")]
    [InlineData("TileRecipeInput", "widget", "title", "chart", "figure", "template")]
    [InlineData("FigureRecipeInput", "measure", "period", "sparkline", "template")]
    [InlineData("SaveWidgetInput", "id", "vehicleId", "title", "chart", "figure", "shared")]
    [InlineData("TrendPeriodInput", "kind", "unit", "count", "since", "whole", "against")]
    [InlineData("ChartConfigInput", "metric", "grouping", "kind", "range", "stacked", "from", "to", "trend", "runningTotal", "compareYearEarlier", "secondMetric", "secondKind")]
    public async Task Schema_InputTypeTakesContractFields(string type, params string[] fields)
    {
        var body = await Query($"{{ __type(name: \"{type}\") {{ inputFields {{ name }} }} }}");

        var names = body.GetProperty("data").GetProperty("__type").GetProperty("inputFields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        foreach (var field in fields) Assert.Contains(field, names);
    }

    [Fact]
    public async Task Schema_LogValuesThatPhotosFillIn_AreOptional_AndTheReviewStatesAreKnown()
    {
        var input = await Query("{ __type(name: \"LogRefuelingInput\") { inputFields { name type { kind } } } }");
        var kinds = input.GetProperty("data").GetProperty("__type").GetProperty("inputFields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type").GetProperty("kind").GetString());
        var states = (await Query("{ __type(name: \"ReviewState\") { enumValues { name } } }"))
            .GetProperty("data").GetProperty("__type").GetProperty("enumValues").EnumerateArray().Select(v => v.GetProperty("name").GetString());

        Assert.Equal(("SCALAR", "SCALAR", "SCALAR", "NON_NULL"), (kinds["volume"], kinds["totalCost"], kinds["odometer"], kinds["date"]));
        Assert.Equal(["NONE", "AWAITING_PHOTOS", "NEEDS_REVIEW", "INCOMPLETE"], states);
    }

    [Fact]
    public async Task Schema_FuelTypeEnumValues()
    {
        var body = await Query("{ __type(name: \"FuelType\") { enumValues { name } } }");

        var values = body.GetProperty("data").GetProperty("__type").GetProperty("enumValues")
            .EnumerateArray().Select(v => v.GetProperty("name").GetString());
        Assert.Equal(["PETROL", "DIESEL", "LPG"], values);
    }

    [Theory]
    [InlineData("VehicleSortField", "NAME", "LICENSE_PLATE", "FUEL_TYPE", "OWNER", "REFUELING_COUNT", "DELETED_AT")]
    [InlineData("SortDirection", "ASC", "DESC")]
    [InlineData("ColorMode", "LIGHT", "DARK", "SYSTEM")]
    [InlineData("SurfaceStyle", "GLOSSY", "TRANSPARENT", "OPAQUE")]
    [InlineData("AuthMode", "NONE", "STANDALONE", "OIDC", "PROXY_HEADER")]
    [InlineData("AccessLevel", "NONE", "VIEW", "EDIT", "DELETE")]
    [InlineData("DistanceUnit", "KILOMETERS", "MILES")]
    [InlineData("VolumeUnit", "LITERS", "US_GALLONS", "IMPERIAL_GALLONS")]
    [InlineData("RefuelingSortField", "DATE", "VOLUME", "TOTAL_COST", "ODOMETER", "PRICE_PER_UNIT", "CONSUMPTION", "CREATED_BY", "VEHICLE", "DELETED_AT")]
    [InlineData("ExpenseSortField", "DATE", "TITLE", "CATEGORY", "AMOUNT", "ODOMETER", "CREATED_BY", "VEHICLE", "DELETED_AT")]
    [InlineData("ChartMetric", "TOTAL_SPEND", "FUEL_COST", "EXPENSE_COST", "FUEL_VOLUME", "DISTANCE", "AVERAGE_CONSUMPTION", "AVERAGE_PRICE_PER_UNIT", "FILL_UPS", "AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT", "COST_PER_DISTANCE", "FUEL_COST_PER_DISTANCE")]
    [InlineData("ChartGrouping", "MONTH", "QUARTER", "YEAR", "CATEGORY")]
    [InlineData("ChartKind", "BAR", "LINE", "AREA", "DONUT")]
    [InlineData("TrendWindowKind", "LAST", "SINCE", "THIS_YEAR", "THIS_MONTH")]
    [InlineData("TrendUnit", "DAYS", "WEEKS", "MONTHS", "YEARS", "FILL_UPS")]
    [InlineData("TrendComparison", "BEFORE", "YEAR_EARLIER", "NONE")]
    [InlineData("FigureMeasure", "TOTAL_SPEND", "FUEL_COST", "EXPENSE_COST", "FUEL_VOLUME", "DISTANCE", "AVERAGE_CONSUMPTION", "AVERAGE_PRICE_PER_UNIT", "FILL_UPS", "AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT", "COST_PER_DISTANCE", "FUEL_COST_PER_DISTANCE", "ODOMETER", "LAST_FILL_UP")]
    [InlineData("TrendFigure", "SPENDING", "COST_PER_DISTANCE", "CONSUMPTION", "DISTANCE")]
    [InlineData("WidgetKind", "CHART", "FIGURE")]
    [InlineData("ChartRange", "LAST1_MONTH", "LAST3_MONTHS", "LAST6_MONTHS", "LAST12_MONTHS", "THIS_YEAR", "LAST_YEAR", "ALL", "CUSTOM")]
    [InlineData("ChartTrend", "NONE", "MOVING_AVERAGE", "LINEAR")]
    [InlineData("ChartSeriesRole", "VALUE", "MOVING_AVERAGE", "LINEAR", "YEAR_EARLIER")]
    [InlineData("ChartUnit", "CURRENCY", "VOLUME", "DISTANCE", "CONSUMPTION", "COUNT", "COST_PER_DISTANCE", "PRICE_PER_VOLUME")]
    [InlineData("NoticeSeverity", "INFO", "WARNING")]
    [InlineData("NotificationKind", "LOG_ACCESS_CHANGED", "VEHICLE_SHARED", "DATA_ACCESS_CHANGED", "DATA_SHARED", "DEFAULT_ACCESS_CHANGED", "RECURRING_DUE_SOON", "RECURRING_OVERDUE", "LOG_FILLED_FROM_PHOTO", "LOG_NOT_FILLED", "SYNC_CHANGE_PARKED", "MORE_ACTIVITY", "BACKUP_FAILED")]
    [InlineData("NotificationEntityType", "INSTANCE", "VEHICLE", "RECURRING_EXPENSE", "USER", "REFUELING", "EXPENSE", "SYNC_CHANGE")]
    [InlineData("SyncChangeKind", "LOG_REFUELING", "UPDATE_REFUELING", "DELETE_REFUELING", "RESTORE_REFUELING", "ADD_EXPENSE", "UPDATE_EXPENSE", "DELETE_EXPENSE", "RESTORE_EXPENSE", "ADD_RECURRING_EXPENSE", "UPDATE_RECURRING_EXPENSE", "DELETE_RECURRING_EXPENSE", "MARK_RECURRING_EXPENSES_DONE", "ADD_VEHICLE", "UPDATE_VEHICLE", "DELETE_VEHICLE", "RESTORE_VEHICLE", "ADD_REFUELING_PHOTO", "REMOVE_REFUELING_PHOTO", "ADD_EXPENSE_PHOTO", "REMOVE_EXPENSE_PHOTO", "SET_VEHICLE_PICTURE", "REMOVE_VEHICLE_PICTURE", "ADD_TYRE_SET", "UPDATE_TYRE_SET", "SET_TYRE_ROUGH_DISTANCE", "RETIRE_TYRE_SET", "UNRETIRE_TYRE_SET", "DELETE_TYRE_SET", "RESTORE_TYRE_SET", "SWAP_TYRES", "CHANGE_TYRE_POSITIONS", "ADD_PAST_TYRE_SWAPS", "ADD_TYRE_STRETCH", "UPDATE_TYRE_STRETCH", "DELETE_TYRE_STRETCH", "RESTORE_TYRE_STRETCH", "SET_TYRE_LOG_DAY")]
    [InlineData("TyreSeason", "SUMMER", "WINTER", "ALL_SEASON")]
    [InlineData("StretchMeasure", "MEASURED", "FROM_FIRST_READING", "NOT_MEASURED")]
    [InlineData("TyreTrashKind", "SET", "STRETCH")]
    [InlineData("SyncResolveAction", "APPLY", "DISCARD")]
    [InlineData("SettingsGroup", "AUTH", "SMTP", "DEFAULTS", "NOTIFICATIONS", "SYNC", "PHOTO_READING", "BACKUPS", "LOGGING", "TELEMETRY")]
    [InlineData("SettingKind", "TEXT", "MULTILINE", "INTEGER", "DECIMAL", "BOOLEAN", "CHOICE", "LIST", "SECRET", "DAYS")]
    [InlineData("SettingSource", "ENVIRONMENT", "FILE", "SHIPPED", "DEFAULT")]
    [InlineData("TimeFormat", "TWENTY_FOUR_HOUR", "TWELVE_HOUR")]
    [InlineData("DateFormat", "ISO", "DAY_MONTH_YEAR_DOTS", "DAY_MONTH_YEAR_SLASHES", "MONTH_DAY_YEAR", "YEAR_MONTH_DAY_DOTS")]
    [InlineData("ServerFolderKind", "PICTURES", "PHOTOS", "BACKUPS", "KEYS", "SETTINGS_FILE")]
    [InlineData("DayOfWeek", "SUNDAY", "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY", "SATURDAY")] // the Backups page names them by these
    public async Task Schema_EnumsExposeContractValues(string type, params string[] expected)
    {
        var body = await Query($"{{ __type(name: \"{type}\") {{ enumValues {{ name }} }} }}");

        var values = body.GetProperty("data").GetProperty("__type").GetProperty("enumValues")
            .EnumerateArray().Select(v => v.GetProperty("name").GetString());
        Assert.Equal(expected, values);
    }

    [Theory]
    [InlineData("vehicles")]
    [InlineData("trash")]
    public async Task Lists_TakeSortingAndPagingArguments(string field)
    {
        var body = await Query("{ __type(name: \"Query\") { fields { name args { name } } } }");

        var args = body.GetProperty("data").GetProperty("__type").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == field).GetProperty("args").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString());
        Assert.Equal(["orderBy", "direction", "skip", "take"], args);
    }

    [Fact]
    public async Task SettingAUsersPassword_AndMakingABackupKeySet_TakeTheRepetition()
    {
        var body = await Query("{ __type(name: \"Mutation\") { fields { name args { name } } } }");
        IEnumerable<string?> ArgsOf(string field) => body.GetProperty("data").GetProperty("__type").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == field).GetProperty("args").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString());

        Assert.Equal(["userId", "newPassword", "newPasswordConfirmation"], ArgsOf("setUserPassword"));
        Assert.Equal(["passphrase", "passphraseConfirmation"], ArgsOf("prepareBackupEncryption"));
    }

    [Fact]
    public async Task Session_AndNotices_ArePublic()
    {
        var body = await Query("{ session { mode } notices { code severity message } }");

        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        Assert.Equal("NONE", body.GetProperty("data").GetProperty("session").GetProperty("mode").GetString());
        Assert.Equal("AUTH_DISABLED", body.GetProperty("data").GetProperty("notices")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheSetupState_IsPublic_AndItsStepsAreClosedOnceItIsCompleted()
    {
        // The contract server runs with its setup off: completed at start, so the code opens nothing.
        var body = await Query("{ setup { pending } }");
        Assert.False(body.TryGetProperty("errors", out _), body.ToString());
        Assert.False(body.GetProperty("data").GetProperty("setup").GetProperty("pending").GetBoolean());

        var closed = await Query("mutation { setupFinish(input: { code: \"ABCD-EFGH-JKLM\", acknowledgeOpenAccess: true }) { restarting } }");
        Assert.Equal("setup.completed", closed.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Theory]
    [InlineData("mutation { holdBackup(file: \"tankstat-backup-20260101T000000Z.zip\", hold: true) }")]
    [InlineData("mutation { holdBackup(file: \"tankstat-backup-20260101T000000Z.zip\", hold: false) }")]
    [InlineData("mutation { deleteBackup(file: \"tankstat-backup-20260101T000000Z.zip\") }")]
    [InlineData("{ backupDetails(file: \"tankstat-backup-20260101T000000Z.zip\") { file held } }")]
    public async Task HoldingReleasingDeletingOrOpeningABackup_IsRefused_WithoutAnAdministrator(string operation)
    {
        // The contract server runs with sign-in off, where no administrator exists: the server itself refuses these, whatever the page shows.
        var body = await Query(operation);

        Assert.Equal("auth.adminRequired", body.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task OpeningABackupOnTheSetupPage_IsRefused_OnceTheSetupIsCompleted()
    {
        var body = await Query("{ setupBackupDetails(code: \"ABCD-EFGH-JKLM\", file: \"tankstat-backup-20260101T000000Z.zip\") { file } }");

        Assert.Equal("setup.completed", body.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString());
    }

    [Fact]
    public async Task Schema_NeverExposesPasswordData()
    {
        var body = (await Query("{ __schema { types { name fields { name } } } }")).ToString().ToLowerInvariant();

        Assert.DoesNotContain("passwordhash", body);
        Assert.DoesNotContain("secrethash", body);
    }

    [Fact]
    public async Task Vehicles_EndToEnd_AgainstRealDatabase()
    {
        var added = await Query("mutation { addVehicle(input: { name: \"Contract car\", fuelType: PETROL }) { id } }");
        var id = added.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString();
        await Query($"mutation {{ logRefueling(input: {{ vehicleId: \"{id}\", date: \"2026-10-01\", volume: 10, totalCost: 20, currency: \"HUF\", odometer: 100, isFullTank: true }}) {{ id }} }}");

        var read = await Query($"{{ vehicle(id: \"{id}\") {{ name }} refuelings(vehicleId: \"{id}\") {{ volume currency }} refuelingCount(vehicleId: \"{id}\") }}");

        Assert.False(read.TryGetProperty("errors", out _), read.ToString());
        Assert.Equal("HUF", Assert.Single(read.GetProperty("data").GetProperty("refuelings").EnumerateArray()).GetProperty("currency").GetString());
        Assert.Equal(1, read.GetProperty("data").GetProperty("refuelingCount").GetInt32());
    }

    [Fact]
    public async Task AKeyFigure_AnyMeasureOverAPeriod_OrAPointInTime_AgainstRealDatabase()
    {
        var added = await Query("mutation { addVehicle(input: { name: \"Figure car\", fuelType: PETROL }) { id } }");
        var id = added.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        foreach (var (reading, volume) in new[] { (1000, 40), (1500, 30) })
            await Query($"mutation {{ logRefueling(input: {{ vehicleId: \"{id}\", date: \"{today}\", volume: {volume}, totalCost: 20000, currency: \"HUF\", odometer: {reading}, isFullTank: true }}) {{ id }} }}");

        var read = await Query($@"{{
            total: vehicleFigure(vehicleId: ""{id}"", measure: TOTAL_SPEND, period: {{ kind: THIS_MONTH, against: NONE }}) {{ recent {{ start end fillUps }} earlier {{ start }} unit currencies {{ currency recent earlier }} }}
            mean: vehicleFigure(vehicleId: ""{id}"", measure: AVERAGE_CONSUMPTION, period: {{ kind: LAST, unit: FILL_UPS, count: 10 }}) {{ unit value {{ recent earlier }} }}
            odometer: vehicleFigure(vehicleId: ""{id}"", measure: ODOMETER) {{ recent {{ start }} unit value {{ recent earlier }} }} }}");

        Assert.False(read.TryGetProperty("errors", out _), read.ToString());
        var data = read.GetProperty("data");
        var total = data.GetProperty("total");
        Assert.Equal((today, 2, JsonValueKind.Null, "CURRENCY"), (total.GetProperty("recent").GetProperty("end").GetString(), total.GetProperty("recent").GetProperty("fillUps").GetInt32(), total.GetProperty("earlier").ValueKind, total.GetProperty("unit").GetString()));
        var huf = Assert.Single(total.GetProperty("currencies").EnumerateArray());
        Assert.Equal(("HUF", 40000m, JsonValueKind.Null), (huf.GetProperty("currency").GetString(), huf.GetProperty("recent").GetDecimal(), huf.GetProperty("earlier").ValueKind));
        Assert.Equal(("CONSUMPTION", 6m), (data.GetProperty("mean").GetProperty("unit").GetString(), data.GetProperty("mean").GetProperty("value").GetProperty("recent").GetDecimal())); // 30 L over 500 km
        var odometer = data.GetProperty("odometer");
        Assert.Equal((JsonValueKind.Null, "DISTANCE", 1500m), (odometer.GetProperty("recent").ValueKind, odometer.GetProperty("unit").GetString(), odometer.GetProperty("value").GetProperty("recent").GetDecimal()));

        var unknown = await Query("{ vehicleFigure(vehicleId: \"00000000-0000-0000-0000-00000000f00d\", measure: ODOMETER) { unit } }");
        Assert.Equal("NOT_FOUND", unknown.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
    }

    // #273: a saved key figure is among the saved widgets, never among the charts an older app reads.
    [Fact]
    public async Task ASavedKeyFigure_IsAmongTheWidgets_NotTheCharts_AgainstRealDatabase()
    {
        var added = await Query("mutation { addVehicle(input: { name: \"Widget car\", fuelType: PETROL }) { id } }");
        var id = added.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString();

        var saved = await Query($@"mutation {{
            figure: saveVehicleWidget(input: {{ vehicleId: ""{id}"", title: ""Spent"", shared: false, figure: {{ measure: TOTAL_SPEND, period: {{ kind: THIS_MONTH }}, sparkline: true, template: ""thisMonth"" }} }}) {{ id widget chart {{ metric }} figure {{ measure sparkline template period {{ kind against }} }} canEdit }}
            chart: saveVehicleWidget(input: {{ vehicleId: ""{id}"", title: ""Bars"", shared: false, chart: {{ metric: FUEL_VOLUME, grouping: MONTH, kind: BAR, range: ALL, stacked: false }} }}) {{ id widget chart {{ metric range }} figure {{ measure }} }} }}");
        Assert.False(saved.TryGetProperty("errors", out _), saved.ToString());
        var figure = saved.GetProperty("data").GetProperty("figure");
        var chart = saved.GetProperty("data").GetProperty("chart");
        Assert.Equal(("FIGURE", JsonValueKind.Null, "TOTAL_SPEND", true, "thisMonth", "THIS_MONTH", "BEFORE", true), (figure.GetProperty("widget").GetString(), figure.GetProperty("chart").ValueKind,
            figure.GetProperty("figure").GetProperty("measure").GetString(), figure.GetProperty("figure").GetProperty("sparkline").GetBoolean(), figure.GetProperty("figure").GetProperty("template").GetString(),
            figure.GetProperty("figure").GetProperty("period").GetProperty("kind").GetString(), figure.GetProperty("figure").GetProperty("period").GetProperty("against").GetString(), figure.GetProperty("canEdit").GetBoolean()));
        Assert.Equal(("CHART", "FUEL_VOLUME", JsonValueKind.Null), (chart.GetProperty("widget").GetString(), chart.GetProperty("chart").GetProperty("metric").GetString(), chart.GetProperty("figure").ValueKind));

        var read = await Query($"{{ vehicleCharts(vehicleId: \"{id}\") {{ id metric }} vehicleWidgets(vehicleId: \"{id}\") {{ id widget }} }}");
        Assert.False(read.TryGetProperty("errors", out _), read.ToString());
        Assert.Equal([chart.GetProperty("id").GetString()], read.GetProperty("data").GetProperty("vehicleCharts").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Equal([(figure.GetProperty("id").GetString(), "FIGURE"), (chart.GetProperty("id").GetString(), "CHART")],
            read.GetProperty("data").GetProperty("vehicleWidgets").EnumerateArray().Select(w => (w.GetProperty("id").GetString(), w.GetProperty("widget").GetString())));
    }

    [Fact]
    public async Task UiSettings_AndVehicleOrder_RoundTrip_AgainstRealDatabase()
    {
        var gridId = $"contract-{Guid.NewGuid():N}"[..20];
        var saved = await Query($"mutation {{ saveGridSettings(input: {{ gridId: \"{gridId}\", order: [\"name\", \"owner\"], hidden: [\"owner\"], pageSize: 10, sortColumn: \"name\", sortDirection: DESC }}) {{ gridId pageSize sortDirection }} }}");
        Assert.False(saved.TryGetProperty("errors", out _), saved.ToString());

        var read = await Query("{ uiSettings { grids { gridId hidden pageSize } } }");
        var grid = read.GetProperty("data").GetProperty("uiSettings").GetProperty("grids").EnumerateArray().Single(g => g.GetProperty("gridId").GetString() == gridId);
        Assert.Equal(10, grid.GetProperty("pageSize").GetInt32());
        Assert.Equal(["owner"], grid.GetProperty("hidden").EnumerateArray().Select(h => h.GetString()));
        Assert.True((await Query($"mutation {{ resetGridSettings(gridId: \"{gridId}\") }}")).GetProperty("data").GetProperty("resetGridSettings").GetBoolean());

        var tag = Guid.NewGuid().ToString("N")[..6];
        var first = (await Query($"mutation {{ addVehicle(input: {{ name: \"Order A {tag}\", fuelType: PETROL }}) {{ id }} }}")).GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString();
        var second = (await Query($"mutation {{ addVehicle(input: {{ name: \"Order B {tag}\", fuelType: PETROL }}) {{ id }} }}")).GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString();
        var arranged = await Query($"mutation {{ setVehicleOrder(vehicleIds: [\"{second}\", \"{first}\"]) }}");
        Assert.False(arranged.TryGetProperty("errors", out _), arranged.ToString());

        var names = (await Query($"{{ myVehicles(search: \"{tag}\") {{ name }} }}")).GetProperty("data").GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToArray();
        Assert.Equal([$"Order B {tag}", $"Order A {tag}"], names);
    }

    [Fact]
    public async Task Vehicles_SoftDeleteRestoreAndEmptyTrash_AgainstRealDatabase()
    {
        var added = await Query("mutation { addVehicle(input: { name: \"Trash car\", fuelType: DIESEL }) { id } }");
        var id = added.GetProperty("data").GetProperty("addVehicle").GetProperty("id").GetString();

        var deleted = await Query($"mutation {{ deleteVehicle(id: \"{id}\") {{ deletedAt }} }}");
        Assert.NotEqual(JsonValueKind.Null, deleted.GetProperty("data").GetProperty("deleteVehicle").GetProperty("deletedAt").ValueKind);
        var trash = await Query("{ trash { id } }");
        Assert.Contains(trash.GetProperty("data").GetProperty("trash").EnumerateArray(), v => v.GetProperty("id").GetString() == id);

        await Query($"mutation {{ restoreVehicle(id: \"{id}\") {{ deletedAt }} }}");
        await Query($"mutation {{ deleteVehicle(id: \"{id}\") {{ id }} }}");
        var emptied = await Query("mutation { emptyTrash }");

        Assert.True(emptied.GetProperty("data").GetProperty("emptyTrash").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Null, (await Query($"{{ vehicle(id: \"{id}\") {{ id }} }}")).GetProperty("data").GetProperty("vehicle").ValueKind);
    }
}

﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using AIDevGallery.Models;
using AIDevGallery.Telemetry;
using Microsoft.Windows.AI.ContentModeration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace AIDevGallery.Utils;

internal class AppData
{
    public required string ModelCachePath { get; set; }
    public required LinkedList<MostRecentlyUsedItem> MostRecentlyUsedItems { get; set; }
    public CustomParametersState? LastCustomParamtersState { get; set; }

    public LinkedList<UsageHistory>? UsageHistoryV2 { get; set; }

    public bool IsDiagnosticDataEnabled { get; set; }

    public bool IsFirstRun { get; set; }

    public bool IsDiagnosticsMessageDismissed { get; set; }

    public AppData()
    {
        IsDiagnosticDataEnabled = !PrivacyConsentHelpers.IsPrivacySensitiveRegion();
        IsFirstRun = true;
        IsDiagnosticsMessageDismissed = false;
    }

    private static string GetConfigFilePath()
    {
        var appDataFolder = ApplicationData.Current.LocalFolder.Path;
        return Path.Combine(appDataFolder, "state.json");
    }

    public static async Task<AppData> GetForApp()
    {
        AppData? appData = null;

        var configFile = GetConfigFilePath();

        try
        {
            if (File.Exists(configFile))
            {
                var file = await File.ReadAllTextAsync(configFile);
                appData = JsonSerializer.Deserialize(file, AppDataSourceGenerationContext.Default.AppData);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            appData ??= GetDefault();
        }

        return appData;
    }

    public async Task SaveAsync()
    {
        var str = JsonSerializer.Serialize(this, AppDataSourceGenerationContext.Default.AppData);
        await File.WriteAllTextAsync(GetConfigFilePath(), str);
    }

    public async Task AddMru(MostRecentlyUsedItem item, string? modelOrApiId = null, HardwareAccelerator? hardwareAccelerator = null)
    {
        UsageHistoryV2 ??= new LinkedList<UsageHistory>();

        foreach (var toRemove in MostRecentlyUsedItems.Where(i => i.ItemId == item.ItemId).ToArray())
        {
            MostRecentlyUsedItems.Remove(toRemove);
        }

        if (MostRecentlyUsedItems.Count > 5)
        {
            MostRecentlyUsedItems.RemoveLast();
        }

        if (!string.IsNullOrWhiteSpace(modelOrApiId))
        {
            var existingItem = UsageHistoryV2.Where(u => u.Id == modelOrApiId).FirstOrDefault();
            if (existingItem != default)
            {
                UsageHistoryV2.Remove(existingItem);
            }

            UsageHistoryV2.AddFirst(new UsageHistory(modelOrApiId, hardwareAccelerator));
        }

        MostRecentlyUsedItems.AddFirst(item);
        await SaveAsync();
    }

    private static AppData GetDefault()
    {
        var homeDirPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheDir = Path.Combine(homeDirPath, ".cache", "aigallery");

        return new AppData
        {
            ModelCachePath = cacheDir,
            MostRecentlyUsedItems = new(),
            UsageHistoryV2 = new()
        };
    }
}

internal class CustomParametersState
{
    public bool? DoSample { get; set; }
    public int? MaxLength { get; set; }
    public int? MinLength { get; set; }
    public int? TopK { get; set; }
    public float? TopP { get; set; }
    public float? Temperature { get; set; }
    public string? UserPrompt { get; set; }
    public string? SystemPrompt { get; set; }
    public SeverityLevel? InputContentModeration { get; set; }
    public SeverityLevel? OutputContentModeration { get; set; }
}

internal record UsageHistory(string Id, HardwareAccelerator? HardwareAccelerator);
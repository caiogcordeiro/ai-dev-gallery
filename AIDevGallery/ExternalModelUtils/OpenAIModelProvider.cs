﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using AIDevGallery.Models;
using AIDevGallery.Utils;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIDevGallery.ExternalModelUtils;

internal class OpenAIModelProvider : IExternalModelProvider
{
    public static OpenAIModelProvider Instance { get; } = new OpenAIModelProvider();

    private const string KeyName = "AI_DEV_GALLERY_OPENAI_API_KEY";
    private IEnumerable<ModelDetails>? _cachedModels;

    public static string? OpenAIKey
    {
        get
        {
            return CredentialManager.ReadCredential(KeyName);
        }
        set
        {
            if (value != null)
            {
                CredentialManager.WriteCredential(KeyName, value);
            }
            else
            {
                CredentialManager.DeleteCredential(KeyName);
            }
        }
    }

    public string Name => "OpenAI";

    public HardwareAccelerator ModelHardwareAccelerator => HardwareAccelerator.OPENAI;

    public List<string> NugetPackageReferences => ["Microsoft.Extensions.AI.OpenAI"];

    public string ProviderDescription => "The model will run on the cloud via OpenAI";

    public string UrlPrefix => "openai://";

    public string Icon => $"OpenAI{AppUtils.GetThemeAssetSuffix()}.svg";

    public string Url => "https://api.openai.com/v1";

    public string? GetDetailsUrl(ModelDetails details)
    {
        return $"https://platform.openai.com/docs/models/{details.Name}";
    }

    public IChatClient? GetIChatClient(string url)
    {
        var modelId = url.Split('/').LastOrDefault();
        return modelId == null ? null : new OpenAIClient(OpenAIKey).GetChatClient(modelId).AsIChatClient();
    }

    public string? IChatClientImplementationNamespace { get; } = "OpenAI";

    public string? GetIChatClientString(string url)
    {
        var modelId = url.Split('/').LastOrDefault();

        return $"new OpenAIClient(\"OPENAI_API_KEY\").GetChatClient(\"{modelId}\").AsIChatClient()";
    }

    public void ClearCachedModels()
    {
        _cachedModels = null;
    }

    public async Task<IEnumerable<ModelDetails>> GetModelsAsync(bool ignoreCache = false, CancellationToken cancelationToken = default)
    {
        if (ignoreCache)
        {
            _cachedModels = null;
        }

        if (_cachedModels != null && _cachedModels.Any())
        {
            return _cachedModels;
        }

        _cachedModels = [];

        try
        {
            OpenAIModelClient client = new OpenAIModelClient(OpenAIKey);

            var models = await client.GetModelsAsync(cancelationToken);

            if (models?.Value == null)
            {
                return _cachedModels;
            }

            _cachedModels = [.. models.Value
                .Where(model => model != null && model.Id != null &&
                    model.Id.Contains("gpt", StringComparison.InvariantCultureIgnoreCase) &&
                    !model.Id.Contains("audio", StringComparison.InvariantCultureIgnoreCase) &&
                    !model.Id.Contains("tts", StringComparison.InvariantCultureIgnoreCase) &&
                    !model.Id.Contains("preview", StringComparison.InvariantCultureIgnoreCase))
                .Select(ToModelDetails)];
        }
        catch
        {
            return _cachedModels;
        }

        return _cachedModels != null && _cachedModels.Any() ? _cachedModels : [];
    }

    private ModelDetails ToModelDetails(OpenAIModel model)
    {
        return new ModelDetails()
        {
            Id = $"openai-{model.Id}",
            Name = model.Id,
            Url = $"{UrlPrefix}{model.Id}",
            Description = $"{model.Id} running on the cloud via OpenAI",
            HardwareAccelerators = [HardwareAccelerator.OPENAI],
            Size = 0,
            SupportedOnQualcomm = true,
            ParameterSize = string.Empty,
        };
    }
}
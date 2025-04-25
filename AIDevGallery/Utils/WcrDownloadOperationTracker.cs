﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using AIDevGallery.Models;
using Microsoft.Windows.AI;
using System.Collections.Generic;
using Windows.Foundation;

namespace AIDevGallery.Utils;

internal class WcrDownloadOperationTracker
{
    public static Dictionary<ModelType, IAsyncOperationWithProgress<AIFeatureReadyResult, double>> Operations { get; } = new();
}
﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.Xml;
using System.Threading.Tasks;
using Imageflow.Fluent;
using Imazen.Common.Helpers;
using Imazen.Common.Storage;
using Microsoft.AspNetCore.Http;

namespace Imageflow.Server
{
    internal class ImageJobInfo
    {
        public ImageJobInfo(HttpContext context, ImageflowMiddlewareOptions options, BlobProvider blobProvider)
        {
            this.options = options;
            Authorized = ProcessRewritesAndAuthorization(context, options);

            if (!Authorized) return;

            HasParams = PathHelpers.SupportedQuerystringKeys.Any(FinalQuery.ContainsKey);


            var extension = Path.GetExtension(FinalVirtualPath);
            if (FinalQuery.TryGetValue("format", out var newExtension))
            {
                extension = newExtension;
            }

            EstimatedFileExtension = PathHelpers.SanitizeImageExtension(extension) ?? "jpg";

            primaryBlob = new BlobFetchCache(FinalVirtualPath, blobProvider);
            allBlobs = new List<BlobFetchCache>(1) {primaryBlob};

            appliedWatermarks = new List<NamedWatermark>();
            
            if (HasParams)
            {
                CommandString = PathHelpers.SerializeCommandString(FinalQuery);

                // Look up watermark names
                if (FinalQuery.TryGetValue("watermark", out var watermarkValues))
                {
                    var watermarkNames = watermarkValues.Split(",").Select(s => s.Trim(' '));
                    foreach (var name in watermarkNames)
                    {
                        var watermark = options.NamedWatermarks.FirstOrDefault(w =>
                            w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (watermark == null)
                        {
                            throw new InvalidOperationException(
                                $"watermark {name} was referenced from the querystring but no watermark by that name is registered with the middleware");
                        }

                        appliedWatermarks.Add(watermark);
                    }
                }
            }

            // After we've populated the defaults, run the event handlers for custom watermarking logic
            var args = new WatermarkingEventArgs(context, FinalVirtualPath, FinalQuery, appliedWatermarks);
            foreach (var handler in options.Watermarking)
            {
                var matches = string.IsNullOrEmpty(handler.PathPrefix) ||
                              FinalVirtualPath.StartsWith(handler.PathPrefix, StringComparison.OrdinalIgnoreCase);
                if (matches) handler.Handler(args);
            }
            appliedWatermarks = args.AppliedWatermarks;

            // Add the watermark source files
            foreach (var w in appliedWatermarks)
            {
                allBlobs.Add(new BlobFetchCache(w.VirtualPath, blobProvider));
            }

            provider = blobProvider;
        }

        public string FinalVirtualPath { get; private set; }
        
        public Dictionary<string,string> FinalQuery { get; private set; }
        public bool HasParams { get; }
        
        public bool Authorized { get; }
        public string CommandString { get; } = "";
        public string EstimatedFileExtension { get; }
        public string AuthorizedMessage { get; set; }

        private readonly BlobProvider provider;

        private readonly List<NamedWatermark> appliedWatermarks;

        private readonly List<BlobFetchCache> allBlobs;
        private readonly BlobFetchCache primaryBlob;
        private readonly ImageflowMiddlewareOptions options;

        private bool ProcessRewritesAndAuthorization(HttpContext context, ImageflowMiddlewareOptions options)
        {
            if (!VerifySignature(context, options))
            {
                AuthorizedMessage = "Invalid request signature";
                return false;
            }
                
            var path = context.Request.Path.Value;
            var args = new UrlEventArgs(context, context.Request.Path.Value, PathHelpers.ToQueryDictionary(context.Request.Query));

            foreach (var handler in options.PreRewriteAuthorization)
            {
                var matches = string.IsNullOrEmpty(handler.PathPrefix) ||
                              path.StartsWith(handler.PathPrefix, StringComparison.OrdinalIgnoreCase);
                if (matches && !handler.Handler(args)) return false;
            }

            if (options.UsePresetsExclusively)
            {
                var firstKey = args.Query.FirstOrDefault().Key;
                
                if (args.Query.Count > 1 || (firstKey != null && firstKey != "preset"))
                {
                    AuthorizedMessage = "Only presets are permitted in the querystring";
                    return false;
                }
            }

            // Parse and apply presets before rewriting
            if (args.Query.TryGetValue("preset", out var presetNames))
            {
                var presetNamesList = presetNames
                    .Split(new char[] {','}, StringSplitOptions.RemoveEmptyEntries);
                foreach (var presetName in presetNamesList)
                {
                    if (options.Presets.TryGetValue(presetName, out var presetOptions))
                    {
                        foreach (var pair in presetOptions.pairs)
                        {
                            if (presetOptions.Priority == PresetPriority.OverrideQuery ||
                                !args.Query.ContainsKey(pair.Key))
                            {
                                args.Query[pair.Key] = pair.Value;
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"The image preset {presetName} was referenced from the querystring but is not registered.");
                    }
                }
            }
            
            // Apply rewrite handlers
            foreach (var handler in options.Rewrite)
            {
                var matches = string.IsNullOrEmpty(handler.PathPrefix) ||
                              path.StartsWith(handler.PathPrefix, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    handler.Handler(args);
                    path = args.VirtualPath;
                }
            }
            
            // Set defaults if keys are missing, but at least 1 supported key is present
            if (PathHelpers.SupportedQuerystringKeys.Any(args.Query.ContainsKey))
            {
                foreach (var pair in options.CommandDefaults)
                {
                    if (!args.Query.ContainsKey(pair.Key))
                    {
                        args.Query[pair.Key] = pair.Value;
                    }
                }
            }

            // Run post-rewrite authorization
            foreach (var handler in options.PostRewriteAuthorization)
            {
                var matches = string.IsNullOrEmpty(handler.PathPrefix) ||
                              path.StartsWith(handler.PathPrefix, StringComparison.OrdinalIgnoreCase);
                if (matches && !handler.Handler(args)) return false;
            }

            FinalVirtualPath = args.VirtualPath;
            FinalQuery = args.Query;
            return true; 
        }

        private bool VerifySignature(HttpContext context, ImageflowMiddlewareOptions options)
        {
            var pathAndQuery = context.Request.PathBase.HasValue
                ? "/" + context.Request.PathBase.Value.TrimStart('/')
                : "";
            pathAndQuery += context.Request.Path.ToString() + context.Request.QueryString.ToString();

            pathAndQuery = Signatures.NormalizePathAndQueryForSigning(pathAndQuery);
            if (context.Request.Query.TryGetValue("signature", out var actualSignature))
            {
                foreach (var key in options.SigningKeys)
                {
                    var expectedSignature = Signatures.SignString(pathAndQuery, key, 16);
                    if (expectedSignature == actualSignature) return true;
                }

                AuthorizedMessage = "Image signature does not match request, or used an invalid signing key.";
                return false;

            }

            // A missing signature is only a problem if they are required
            if (!options.RequireRequestSignature) return true;
            
            AuthorizedMessage = "Image requests must be signed. No &signature query key found. ";
            return false;

        }


        public bool PrimaryBlobMayExist()
        {
            return primaryBlob.GetBlobResult() != null;
        }

        public bool NeedsCaching()
        {
            return HasParams || primaryBlob?.GetBlobResult()?.IsFile == false;
        }
      
        public Task<IBlobData> GetPrimaryBlob()
        {
            return primaryBlob.GetBlob();
        }

        private string HashStrings(IEnumerable<string> strings)
        {
            return PathHelpers.Base64Hash(string.Join('|',strings));
        }

        internal async Task CopyPrimaryBlobToAsync(Stream stream)
        {

            await using var sourceStream = (await GetPrimaryBlob()).OpenRead();
            var oldPosition = stream.Position;
            await sourceStream.CopyToAsync(stream);
            if (stream.Position - oldPosition == 0)
            {
                throw new InvalidOperationException("Source blob has zero bytes; will not proxy.");
            }
        }

        internal async Task<byte[]> GetPrimaryBlobBytesAsync()
        {
            await using var sourceStream = (await GetPrimaryBlob()).OpenRead();
            var ms = new MemoryStream((int)sourceStream.Length);
            await sourceStream.CopyToAsync(ms);
            var buffer = ms.ToArray();
            if (buffer.Length == 0)
            {
                throw new InvalidOperationException("Source blob has length of zero bytes; will not proxy.");
            }
            return buffer;
        }

        private string[] serializedWatermarkConfigs = null;
        private IEnumerable<string> SerializeWatermarkConfigs()
        {
            if (serializedWatermarkConfigs != null) return serializedWatermarkConfigs;
            if (appliedWatermarks == null) return Enumerable.Empty<string>();
            serializedWatermarkConfigs = appliedWatermarks.Select(w => w.Serialized()).ToArray();
            return serializedWatermarkConfigs;
        }

        public async Task<string> GetFastCacheKey()
        {
            // Only get DateTime values from local files
            var dateTimes = await Task.WhenAll(
                allBlobs
                    .Where(b => b.GetBlobResult()?.IsFile == true)
                    .Select(async b =>
                        (await b.GetBlob())?.LastModifiedDateUtc?.ToBinary().ToString()));
            
            return HashStrings(new string[] {FinalVirtualPath, CommandString}.Concat(dateTimes).Concat(SerializeWatermarkConfigs()));
        }

        public override string ToString()
        {
            return CommandString;
        }

        public async Task<string> GetExactCacheKey()
        {
            var dateTimes = await Task.WhenAll(
                allBlobs
                    .Select(async b =>
                        (await b.GetBlob())?.LastModifiedDateUtc?.ToBinary().ToString()));
            
            return HashStrings(new string[] {FinalVirtualPath, CommandString}.Concat(dateTimes).Concat(SerializeWatermarkConfigs()));
        }

        public async Task<ImageData> ProcessUncached()
        {
            //Fetch all blobs simultaneously
            var blobs = await Task.WhenAll(
                allBlobs
                    .Select(async b =>
                        (await b.GetBlob())));
            
            //Add all StreamSources
            List<InputWatermark> watermarks = null;
            if (appliedWatermarks != null)
            {
                watermarks = new List<InputWatermark>(appliedWatermarks.Count);
                watermarks.AddRange(
                    appliedWatermarks.Select((t, i) =>
                    {
                        if (blobs[i + 1] == null)
                            throw new BlobMissingException(
                                $"Cannot locate watermark \"{t.Name}\" at virtual path \"{t.VirtualPath}\"");
                        return new InputWatermark(
                            new StreamSource(blobs[i + 1].OpenRead(), true),
                            t.Watermark);
                    }));
            }

            using var buildJob = new ImageJob();
            var jobResult = await buildJob.BuildCommandString(
                    new StreamSource(blobs[0].OpenRead(), true),
                    new BytesDestination(), CommandString, watermarks)
                .Finish()
                .SetSecurityOptions(options.JobSecurityOptions)
                .InProcessAsync();

            var bytes = jobResult.First.TryGetBytes();

            if (!bytes.HasValue || bytes.Value.Count < 1)
            {
                throw new InvalidOperationException("Image job returned zero bytes.");
            }

            return new ImageData
            {
                ContentType = jobResult.First.PreferredMimeType,
                FileExtension = jobResult.First.PreferredExtension,
                ResultBytes = bytes.Value
            };
        }
    }
}

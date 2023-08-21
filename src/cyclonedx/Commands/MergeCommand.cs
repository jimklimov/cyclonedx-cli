// This file is part of CycloneDX CLI Tool
//
// Licensed under the Apache License, Version 2.0 (the “License”);
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an “AS IS” BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) OWASP Foundation. All Rights Reserved.
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using CycloneDX.Models;
using CycloneDX.Utils;
using System.IO;
using System.Collections.Immutable;

namespace CycloneDX.Cli.Commands
{
    public static class MergeCommand
    {
        public static void Configure(RootCommand rootCommand)
        {
            Contract.Requires(rootCommand != null);
            var subCommand = new Command("merge", "Merge two or more BOMs");
            subCommand.Add(new Option<List<string>>("--input-files", "Input BOM filenames (separate filenames with a space)."));
            subCommand.Add(new Option<List<string>>("--input-files-list", "One or more text file(s) with input BOM filenames (one per line)."));
            subCommand.Add(new Option<List<string>>("--input-files-nul-list", "One or more text-like file(s) with input BOM filenames (separated by 0x00 characters)."));
            subCommand.Add(new Option<string>("--output-file", "Output BOM filename, will write to stdout if no value provided."));
            subCommand.Add(new Option<CycloneDXBomFormat>("--input-format", "Specify input file format."));
            subCommand.Add(new Option<CycloneDXBomFormat>("--output-format", "Specify output file format."));
            subCommand.Add(new Option<bool>("--hierarchical", "Perform a hierarchical merge."));
            subCommand.Add(new Option<string>("--group", "Provide the group of software the merged BOM describes."));
            subCommand.Add(new Option<string>("--name", "Provide the name of software the merged BOM describes (required for hierarchical merging)."));
            subCommand.Add(new Option<string>("--version", "Provide the version of software the merged BOM describes (required for hierarchical merging)."));
            subCommand.Add(new Option<bool>("--validate-output", "Perform validation of the resulting document before writing it, and do not write if it fails."));
            subCommand.Add(new Option<bool>("--validate-output-relaxed", "Perform validation of the resulting document, and still write the file for troubleshooting if it fails."));
            subCommand.Handler = CommandHandler.Create<MergeCommandOptions>(Merge);
            rootCommand.Add(subCommand);
        }

        public static async Task<int> Merge(MergeCommandOptions options)
        {
            Contract.Requires(options != null);
            var outputToConsole = string.IsNullOrEmpty(options.OutputFile);

            if (options.Hierarchical && (options.Name is null || options.Version is null))
            {
                Console.WriteLine($"Name and version must be specified when performing a hierarchical merge.");
                return (int)ExitCode.ParameterValidationError;
            }

            if (options.OutputFormat == CycloneDXBomFormat.autodetect) options.OutputFormat = CliUtils.AutoDetectBomFormat(options.OutputFile);
            if (options.OutputFormat == CycloneDXBomFormat.autodetect)
            {
                Console.WriteLine($"Unable to auto-detect output format");
                return (int)ExitCode.ParameterValidationError;
            }

            Console.WriteLine($"Loading input document(s)...");
            var inputBoms = await InputBoms(DetermineInputFiles(options), options.InputFormat, outputToConsole).ConfigureAwait(false);

            int countBoms = 0;
            int countComponents = 0;
            foreach (Bom inputBom in inputBoms)
            {
                countBoms++;
                if (inputBom is null)
                {
                    continue;
                }
                if (inputBom.Components is not null && inputBom.Components.Count > 0)
                {
                    countComponents += inputBom.Components.Count;
                }
                if (inputBom.Metadata?.Component is not null)
                {
                    countComponents++;
                }
            }
            Console.WriteLine($"Loaded {countBoms} input document(s) with {countComponents} components originally (overlaps to merge are possible)");

            Component bomSubject = null;
            if (options.Group != null || options.Name != null || options.Version != null)
                bomSubject = new Component
                {
                    Type = Component.Classification.Application,
                    Group = options.Group,
                    Name = options.Name,
                    Version = options.Version,
                };

            Bom outputBom;
            Console.WriteLine($"Beginning merge processing (this can take a while)");
            if (options.Hierarchical)
            {
                outputBom = CycloneDXUtils.HierarchicalMerge(inputBoms, bomSubject);
            }
            else
            {
                outputBom = CycloneDXUtils.FlatMerge(inputBoms, bomSubject);
                if (outputBom.Metadata is null)
                {
                    outputBom.Metadata = new Metadata();
                }

                if (bomSubject == null)
                {
                    // otherwise use the first non-null component from the input
                    // BOMs as the default; note CleanupMetadataComponent() below
                    // to ensure that such bom-ref exists in the document only once.
                    foreach (var bom in inputBoms)
                    {
                        if (bom.Metadata != null && bom.Metadata.Component != null)
                        {
                            outputBom.Metadata.Component = bom.Metadata.Component;
                            break;
                        }
                    }
                }
            }

            outputBom = CycloneDXUtils.CleanupMetadataComponent(outputBom);
            outputBom = CycloneDXUtils.CleanupEmptyLists(outputBom);

            outputBom.Version = 1;
            outputBom.SerialNumber = "urn:uuid:" + System.Guid.NewGuid().ToString();
            if (outputBom.Metadata is null)
            {
                outputBom.Metadata = new Metadata();
            }
            if (outputBom.Metadata.Timestamp is null)
            {
                outputBom.Metadata.Timestamp = DateTime.Now;
            }

            ValidationResult validationResult = null;
            if (options.ValidateOutput || options.ValidateOutputRelaxed)
            {
                Console.WriteLine("Validating merged BOM...");

                // TOTHINK: let it pick versions (no arg) if current does not cut it...
                // else SpecificationVersionHelpers.CurrentVersion ?
                validationResult = Json.Validator.Validate(Json.Serializer.Serialize(outputBom), outputBom.SpecVersion);

                if (validationResult.Messages != null)
                {
                    foreach (var message in validationResult.Messages)
                    {
                        Console.WriteLine(message);
                    }
                }

                if (validationResult.Valid)
                {
                    Console.WriteLine("Merged BOM validated successfully.");
                }
                else
                {
                    Console.WriteLine("Merged BOM is not valid.");
                    if (options.ValidateOutput)
                    {
                        // Not-relaxed mode: abort!
                        Console.WriteLine($"    Total {outputBom.Components?.Count ?? 0} components");
                        return (int)ExitCode.SignatureFailedVerification;
                    }
                }
            }

            if (!outputToConsole)
            {
                Console.WriteLine("Writing output file...");
                Console.WriteLine($"    Total {outputBom.Components?.Count ?? 0} components");
            }

            int res = await CliUtils.OutputBomHelper(outputBom, options.OutputFormat, options.OutputFile).ConfigureAwait(false);
            if (validationResult != null && (!validationResult.Valid))
            {
                // Relaxed mode: abort after writing the file!
                return (int)ExitCode.SignatureFailedVerification;
            }
            return res;
        }

        private static List<string> DetermineInputFiles(MergeCommandOptions options)
        {
            List<string> InputFiles;
            if (options.InputFiles != null)
            {
                InputFiles = (List<string>)options.InputFiles;
            }
            else
            {
                InputFiles = new List<string>();
            }

            Console.WriteLine($"Got " + InputFiles.Count + " individual input file name(s): ['" + string.Join("', '", InputFiles) + "']");
            if (options.InputFilesList != null)
            {
                // For some reason, without an immutable list this claims
                // modifications of the iterable during iteration and fails:
                ImmutableList<string> InputFilesList = options.InputFilesList.ToImmutableList();
                Console.WriteLine($"Processing " + InputFilesList.Count + " file(s) with list of actual input file names: ['" + string.Join("', '", InputFilesList) + "']");
                foreach (string OneInputFileList in InputFilesList)
                {
                    Console.WriteLine($"Adding to input file list from " + OneInputFileList);
                    string[] lines = File.ReadAllLines(OneInputFileList);
                    int count = 0;
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        if (InputFiles.Contains(line)) continue;
                        InputFiles.Add(line);
                        count++;
                    }
                    Console.WriteLine($"Got " + count + " new entries from " + OneInputFileList);
                }
            }

            if (options.InputFilesNulList != null)
            {
                ImmutableList<string> InputFilesNulList = options.InputFilesNulList.ToImmutableList();
                Console.WriteLine($"Processing " + InputFilesNulList.Count + " file(s) with NUL-separated list of actual input file names: ['" + string.Join("', '", InputFilesNulList) + "']");
                foreach (string OneInputFileList in InputFilesNulList)
                {
                    Console.WriteLine($"Adding to input file list from " + OneInputFileList);
                    string[] lines = File.ReadAllText(OneInputFileList).Split('\0');
                    int count = 0;
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        if (InputFiles.Contains(line)) continue;
                        InputFiles.Add(line);
                        count++;
                    }
                    Console.WriteLine($"Got " + count + " new entries from " + OneInputFileList);
                }
            }

            if (InputFiles.Count == 0)
            {
                // Revert to legacy (error-handling) behavior below
                // in case the parameter was not passed
                InputFiles = null;
            } else {
                Console.WriteLine($"Determined " + InputFiles.Count + " input files to merge");
            }

            return InputFiles;
        }

        private static async Task<IEnumerable<Bom>> InputBoms(IEnumerable<string> inputFilenames, CycloneDXBomFormat inputFormat, bool outputToConsole)
        {
            var boms = new List<Bom>();
            foreach (var inputFilename in inputFilenames)
            {
                if (!outputToConsole) Console.WriteLine($"Processing input file {inputFilename}");
                var inputBom = await CliUtils.InputBomHelper(inputFilename, inputFormat).ConfigureAwait(false);
                if (inputBom.Components != null && !outputToConsole)
                    Console.WriteLine($"    Contains {inputBom.Components.Count} components");
                //TODO: figure out how to implement async iterators, if possible at all
                boms.Add(inputBom);
            }
            return boms;
        }
    }
}

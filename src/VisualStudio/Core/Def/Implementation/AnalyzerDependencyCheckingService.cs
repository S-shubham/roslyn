﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.LanguageServices.Implementation.TaskList;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation
{
    [Export(typeof(AnalyzerDependencyCheckingService))]
    internal sealed class AnalyzerDependencyCheckingService
    {
        private static readonly object s_dependencyConflictErrorId = new object();

        private readonly VisualStudioWorkspaceImpl _workspace;
        private readonly HostDiagnosticUpdateSource _updateSource;
        private readonly BindingRedirectionService _bindingRedirectionService;

        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Task<AnalyzerDependencyResults> _task = Task.FromResult((AnalyzerDependencyResults)null);
        private ImmutableHashSet<string> _analyzerPaths = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

        [ImportingConstructor]
        public AnalyzerDependencyCheckingService(
            VisualStudioWorkspaceImpl workspace,
            HostDiagnosticUpdateSource updateSource)
        {
            _workspace = workspace;
            _updateSource = updateSource;
            _bindingRedirectionService = new BindingRedirectionService();
        }

        public async void CheckForConflictsAsync()
        {
            AnalyzerDependencyResults results = null;
            try
            {
                results = await GetConflictsAsync().ConfigureAwait(continueOnCapturedContext: true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (results == null)
            {
                return;
            }

            var builder = ImmutableArray.CreateBuilder<DiagnosticData>();

            var conflicts = results.Conflicts;
            var missingDependencies = results.MissingDependencies;

            foreach (var project in _workspace.ProjectTracker.Projects)
            {
                builder.Clear();

                foreach (var conflict in conflicts)
                {
                    if (project.CurrentProjectAnalyzersContains(conflict.AnalyzerFilePath1) ||
                        project.CurrentProjectAnalyzersContains(conflict.AnalyzerFilePath2))
                    {
                        builder.Add(CreateDiagnostic(project.Id, conflict));
                    }
                }

                foreach (var missingDependency in missingDependencies)
                {
                    if (project.CurrentProjectAnalyzersContains(missingDependency.AnalyzerPath))
                    {
                        builder.Add(CreateDiagnostic(project.Id, missingDependency));
                    }
                }

                _updateSource.UpdateDiagnosticsForProject(project.Id, s_dependencyConflictErrorId, builder.ToImmutable());
            }

            foreach (var conflict in conflicts)
            {
                LogConflict(conflict);
            }

            foreach (var missingDependency in missingDependencies)
            {
                LogMissingDependency(missingDependency);
            }
        }

        private void LogConflict(AnalyzerDependencyConflict conflict)
        {
            Logger.Log(
                FunctionId.AnalyzerDependencyCheckingService_LogConflict,
                KeyValueLogMessage.Create(m =>
                {
                    m["Identity"] = conflict.Identity.ToString();
                    m["Analyzer1"] = conflict.AnalyzerFilePath1;
                    m["Analyzer2"] = conflict.AnalyzerFilePath2;
                }));
        }

        private void LogMissingDependency(MissingAnalyzerDependency missingDependency)
        {
            Logger.Log(
                FunctionId.AnalyzerDependencyCheckingService_LogMissingDependency,
                KeyValueLogMessage.Create(m =>
                {
                    m["Analyzer"] = missingDependency.AnalyzerPath;
                    m["Identity"] = missingDependency.DependencyIdentity;
                }));
        }

        private DiagnosticData CreateDiagnostic(ProjectId projectId, AnalyzerDependencyConflict conflict)
        {
            string id = ServicesVSResources.WRN_AnalyzerDependencyConflictId;
            string category = ServicesVSResources.ErrorCategory;
            string message = string.Format(
                ServicesVSResources.WRN_AnalyzerDependencyConflictMessage,
                conflict.AnalyzerFilePath1,
                conflict.AnalyzerFilePath2,
                conflict.Identity.ToString());

            DiagnosticData data = new DiagnosticData(
                id,
                category,
                message,
                ServicesVSResources.WRN_AnalyzerDependencyConflictMessage,
                severity: DiagnosticSeverity.Warning,
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                warningLevel: 0,
                customTags: ImmutableArray<string>.Empty,
                properties: ImmutableDictionary<string, string>.Empty,
                workspace: _workspace,
                projectId: projectId);

            return data;
        }

        private DiagnosticData CreateDiagnostic(ProjectId projectId, MissingAnalyzerDependency missingDependency)
        {
            string id = ServicesVSResources.WRN_MissingAnalyzerReferenceId;
            string category = ServicesVSResources.ErrorCategory;
            string message = string.Format(
                ServicesVSResources.WRN_MissingAnalyzerReferenceMessage,
                missingDependency.AnalyzerPath,
                missingDependency.DependencyIdentity.ToString());

            DiagnosticData data = new DiagnosticData(
                id,
                category,
                message,
                ServicesVSResources.WRN_MissingAnalyzerReferenceMessage,
                severity: DiagnosticSeverity.Warning,
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                warningLevel: 0,
                customTags: ImmutableArray<string>.Empty,
                properties: ImmutableDictionary<string, string>.Empty,
                workspace: _workspace,
                projectId: projectId);

            return data;
        }

        private Task<AnalyzerDependencyResults> GetConflictsAsync()
        {
            ImmutableHashSet<string> currentAnalyzerPaths = _workspace.CurrentSolution
                .Projects
                .SelectMany(p => p.AnalyzerReferences)
                .OfType<AnalyzerFileReference>()
                .Select(a => a.FullPath)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

            if (currentAnalyzerPaths.SetEquals(_analyzerPaths))
            {
                return _task;
            }

            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();
            _analyzerPaths = currentAnalyzerPaths;

            _task = _task.SafeContinueWith(_ =>
            {

                IEnumerable<AssemblyIdentity> loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => AssemblyIdentity.FromAssemblyDefinition(assembly));
                return new AnalyzerDependencyChecker(currentAnalyzerPaths, loadedAssemblies, _bindingRedirectionService).Run(_cancellationTokenSource.Token);
            },
            TaskScheduler.Default);

            return _task;
        }

        private class BindingRedirectionService : IBindingRedirectionService
        {
            public AssemblyIdentity ApplyBindingRedirects(AssemblyIdentity originalIdentity)
            {
                string redirectedAssemblyName = AppDomain.CurrentDomain.ApplyPolicy(originalIdentity.ToString());

                AssemblyIdentity redirectedAssemblyIdentity;
                if (AssemblyIdentity.TryParseDisplayName(redirectedAssemblyName, out redirectedAssemblyIdentity))
                {
                    return redirectedAssemblyIdentity;
                }

                return originalIdentity;
            }
        }
    }
}

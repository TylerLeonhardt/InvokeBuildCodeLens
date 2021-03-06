//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Language;

namespace Microsoft.PowerShell.EditorServices.Symbols
{
    /// <summary>
    /// Provides an IDocumentSymbolProvider implementation for
    /// enumerating task symbols in PSake/InvokeBuild files.
    /// </summary>
    public class PSakeDocumentSymbolProvider : FeatureProviderBase, IDocumentSymbolProvider
    {
        public enum BuildRunnerOptions
        {
            PSake,
            InvokeBuild
        }

        public BuildRunnerOptions BuildRunner { get; private set; }

        IEnumerable<SymbolReference> IDocumentSymbolProvider.ProvideDocumentSymbols(
            ScriptFile scriptFile)
        {
            if (scriptFile.FilePath.EndsWith(
                    "build.ps1",
                    StringComparison.OrdinalIgnoreCase))
            {
                BuildRunner = BuildRunnerOptions.InvokeBuild;
            } else if (scriptFile.FilePath.EndsWith(
                    "psakefile.ps1",
                    StringComparison.OrdinalIgnoreCase))
            {
                BuildRunner = BuildRunnerOptions.PSake;
            }
            else
            {
                return Enumerable.Empty<SymbolReference>();
            }



            // Find plausible PSake commands
            IEnumerable<Ast> commandAsts = scriptFile.ScriptAst.FindAll(IsNamedCommandWithArguments, true);

            return commandAsts.OfType<CommandAst>()
                              .Where(IsPSakeCommand)
                              .Select(ast => ConvertPSakeAstToSymbolReference(scriptFile, ast));
        }

        /// <summary>
        /// Convert a CommandAst known to represent a PSake command and a reference to the scriptfile
        /// it is in into symbol representing a PSake call for code lens
        /// </summary>
        /// <param name="scriptFile">the scriptfile the PSake call occurs in</param>
        /// <param name="psakeCommandAst">the CommandAst representing the PSake call</param>
        /// <returns>a symbol representing the PSake call containing metadata for CodeLens to use</returns>
        private PSakeSymbolReference ConvertPSakeAstToSymbolReference(ScriptFile scriptFile, CommandAst psakeCommandAst)
        {
            string commandStartLine = scriptFile.GetLine(psakeCommandAst.Extent.StartLineNumber);
            if (!string.Equals(psakeCommandAst.GetCommandName(), "task", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Search for a name for the task
            // If the task has more than one argument for names, we set it to null
            string taskName = null;
            bool alreadySawName = false;
            for (int i = 1; i < psakeCommandAst.CommandElements.Count; i++)
            {
                CommandElementAst currentCommandElement = psakeCommandAst.CommandElements[i];

                // Check for an explicit "-Name" parameter
                if (currentCommandElement is CommandParameterAst parameterAst)
                {
                    if (string.Equals(parameterAst.ParameterName, "name", StringComparison.OrdinalIgnoreCase))
                    {
                        // Found -Name parameter, move to next element which is the argument for -Name
                        i++;

                        if (!alreadySawName && TryGetNameArgument(psakeCommandAst.CommandElements[i], out taskName))
                        {
                            alreadySawName = true;
                        }

                        continue;
                    }
                    
                    // The depends parameter is commonly (and only) used in PSake builds, so if we see it, use that.
                    if (string.Equals(parameterAst.ParameterName, "depends", StringComparison.OrdinalIgnoreCase))
                    {
                        BuildRunner = BuildRunnerOptions.PSake;
                    }

                    i++;
                    continue;
                }

                // Otherwise, if an argument is given with no parameter, we assume it's the name
                // If we've already seen a name, we set the name to null
                if (!alreadySawName && TryGetNameArgument(psakeCommandAst.CommandElements[i], out taskName))
                {
                    alreadySawName = true;
                }
            }

            return new PSakeSymbolReference(
                scriptFile,
                commandStartLine,
                taskName,
                psakeCommandAst.Extent
            );
        }

        /// <summary>
        /// Test if the given Ast is a regular CommandAst with arguments
        /// </summary>
        /// <param name="ast">the PowerShell Ast to test</param>
        /// <returns>true if the Ast represents a PowerShell command with arguments, false otherwise</returns>
        private static bool IsNamedCommandWithArguments(Ast ast)
        {
            return ast is CommandAst commandAst &&
                commandAst.InvocationOperator != TokenKind.Dot &&
                string.Equals(commandAst.GetCommandName(), "task", StringComparison.OrdinalIgnoreCase) &&
                commandAst.CommandElements.Count >= 2;
        }

        /// <summary>
        /// Test whether the given CommandAst represents a PSake command
        /// </summary>
        /// <param name="commandAst">the CommandAst to test</param>
        /// <returns>true if the CommandAst represents a PSake command, false otherwise</returns>
        private static bool IsPSakeCommand(CommandAst commandAst)
        {
            if (commandAst == null)
            {
                return false;
            }

            // Ensure the first word is a task keyword
            if (!string.Equals(commandAst.GetCommandName(), "task", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool TryGetNameArgument(CommandElementAst commandElementAst, out string taskName)
        {
            taskName = null;

            if (commandElementAst is StringConstantExpressionAst taskNameStrAst)
            {
                taskName = taskNameStrAst.Value;
                return true;
            }

            return (commandElementAst is ExpandableStringExpressionAst);
        }
    }

    /// <summary>
    /// Provides a specialization of SymbolReference containing
    /// extra information about PSake task symbols.
    /// </summary>
    public class PSakeSymbolReference : SymbolReference
    {
        private static readonly char[] DefinitionTrimChars = new char[] { ' ', '{' };

        /// <summary>
        /// Gets the name of the task
        /// </summary>
        public string TaskName { get; private set; }

        internal PSakeSymbolReference(
            ScriptFile scriptFile,
            string taskLine,
            string taskName,
            IScriptExtent scriptExtent)
                : base(
                    SymbolType.Function,
                    taskLine.TrimEnd(DefinitionTrimChars),
                    scriptExtent,
                    scriptFile.FilePath,
                    taskLine)
        {
            this.TaskName = taskName;
        }
    }
}

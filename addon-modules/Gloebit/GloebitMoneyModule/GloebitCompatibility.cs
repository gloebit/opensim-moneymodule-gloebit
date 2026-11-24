/*
 * GloebitCompatibility.cs is part of OpenSim-MoneyModule-Gloebit
 * Copyright (C) 2026 Gloebit LLC
 *
 * OpenSim-MoneyModule-Gloebit is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * OpenSim-MoneyModule-Gloebit is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with OpenSim-MoneyModule-Gloebit.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Gloebit.GloebitMoneyModule {

    /// <summary>
    /// GloebitCompatibility reports when this DLL was built against a different OpenSim
    /// than the one running it, which otherwise shows up later as a MissingMethodException
    /// that can take down the region.
    /// </summary>
    public static class GloebitCompatibility {

        /// <summary>
        /// MAX_REPORTED caps how many failures each message lists.
        /// </summary>
        public const int MAX_REPORTED = 10;

        /// <summary>
        /// RELEASES_URL is where to find a DLL matching the running OpenSim.
        /// </summary>
        public const string RELEASES_URL = "https://github.com/gloebit/opensim-moneymodule-gloebit/releases/latest";

        /// <summary>
        /// CompatibilityReport holds what Check found: Errors for what will fail when used,
        /// Warnings for what might.
        /// </summary>
        public class CompatibilityReport {
            public List<string> Errors = new List<string>();   // OpenSim code this DLL needs is missing
            public List<string> Warnings = new List<string>(); // version mismatch, missing non-OpenSim assemblies, other failures
        }

        /// <summary>
        /// Check returns what it finds about this DLL and the running OpenSim.
        ///
        /// It compares the OpenSim version this DLL was built against with the running one:
        /// VersionInfo.VersionNumber and VERSION_FLAVOUR are constants, so the compiler copied
        /// the built-against values into this DLL, while reading the same fields by reflection
        /// returns the running OpenSim's values.  A mismatch alone is a warning: builds that
        /// differ in version can still share every API this DLL uses.
        ///
        /// Then it loads every type in this DLL and JIT-compiles every method with
        /// RuntimeHelpers.PrepareMethod.  Compiling a method resolves the members it calls, so
        /// an OpenSim member that was changed or removed is reported here, at startup, instead
        /// of when a user first triggers it.  This also catches builds that share a version
        /// label but differ in API.  It can't catch changed behavior behind an unchanged
        /// signature.
        ///
        /// Failures are grouped by cause: a missing OpenSim member or type is an error (the
        /// DLL doesn't match this OpenSim); a missing non-OpenSim assembly (e.g. a database
        /// provider this sim doesn't use) and anything else are warnings.
        ///
        /// Check catches its own exceptions and reports them as warnings.
        /// </summary>
        public static CompatibilityReport Check() {
            CompatibilityReport report = new CompatibilityReport();
            try {
                string versionWarning = CheckVersion();
                if (versionWarning != null) {
                    report.Warnings.Add(versionWarning);
                }
            } catch (Exception e) {
                report.Warnings.Add(String.Format("Could not compare OpenSim versions: {0}", e.Message));
            }
            try {
                Findings findings = FindFailures();
                if (findings.OpenSimMismatch.Count > 0) {
                    report.Errors.Add(String.Format(
                        "{0} part(s) of this Gloebit DLL use OpenSim code that this OpenSim doesn't have, so they will fail when used.  First {1}: {2}.  This DLL was built for a different OpenSim build; download the one matching `show version` from {3}",
                        findings.OpenSimMismatch.Count, Math.Min(findings.OpenSimMismatch.Count, MAX_REPORTED),
                        String.Join("; ", Head(findings.OpenSimMismatch)), RELEASES_URL));
                }
                if (findings.MissingAssemblies.Count > 0) {
                    report.Warnings.Add(String.Format(
                        "This Gloebit DLL references assemblies that couldn't be loaded: {0}.  Gloebit features that use them will fail; if they belong to something this sim doesn't use (e.g. another database provider), this is harmless",
                        String.Join("; ", Head(new List<string>(findings.MissingAssemblies.Keys)))));
                }
                if (findings.Other.Count > 0) {
                    report.Warnings.Add(String.Format(
                        "{0} part(s) of this Gloebit DLL could not be compiled.  First {1}: {2}",
                        findings.Other.Count, Math.Min(findings.Other.Count, MAX_REPORTED),
                        String.Join("; ", Head(findings.Other))));
                }
            } catch (Exception e) {
                report.Warnings.Add(String.Format("Could not check this DLL's references: {0}", e.Message));
            }
            return report;
        }

        /// <summary>
        /// Findings groups the failures FindFailures saw by cause.
        /// </summary>
        private class Findings {
            public List<string> OpenSimMismatch = new List<string>(); // "Type.Method: message" or a type-load message
            public Dictionary<string, bool> MissingAssemblies = new Dictionary<string, bool>(); // assembly names, as a set
            public List<string> Other = new List<string>();
        }

        /// <summary>
        /// Head returns the first MAX_REPORTED items of items.
        /// </summary>
        private static List<string> Head(List<string> items) {
            return items.GetRange(0, Math.Min(items.Count, MAX_REPORTED));
        }

        /// <summary>
        /// CheckVersion returns a warning when the OpenSim version number or flavour this DLL
        /// was built against differs from the running OpenSim's, or null when they match.
        /// </summary>
        private static string CheckVersion() {
            string builtNumber = OpenSim.VersionInfo.VersionNumber;
            int builtFlavour = (int)OpenSim.VersionInfo.VERSION_FLAVOUR;

            Type versionInfo = typeof(OpenSim.VersionInfo);
            object runningNumber = versionInfo.GetField("VersionNumber").GetRawConstantValue();
            object runningFlavour = versionInfo.GetField("VERSION_FLAVOUR").GetRawConstantValue();

            if (builtNumber == (string)runningNumber && builtFlavour == Convert.ToInt32(runningFlavour)) {
                return null;
            }
            return String.Format(
                "This Gloebit DLL was built for OpenSim {0} {1}, but this is {2}.  If Gloebit features fail, download the DLL matching `show version` from {3}",
                builtNumber, (OpenSim.VersionInfo.Flavour)builtFlavour,
                OpenSim.VersionInfo.Version.Trim(), RELEASES_URL);
        }

        /// <summary>
        /// FindFailures loads each type declared in this assembly and JIT-compiles each of its
        /// methods and constructors, recording every failure by cause and continuing.  Generic
        /// and abstract methods are skipped: they have no single compilable body.
        /// </summary>
        private static Findings FindFailures() {
            Findings findings = new Findings();
            Type[] types;
            try {
                types = typeof(GloebitCompatibility).Assembly.GetTypes();
            } catch (ReflectionTypeLoadException e) {
                types = e.Types;
                foreach (Exception loaderException in e.LoaderExceptions) {
                    if (loaderException != null) {
                        Record(findings, null, loaderException);
                    }
                }
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type type in types) {
                if (type == null || type.ContainsGenericParameters) {
                    continue;
                }
                List<MethodBase> methods = new List<MethodBase>();
                try {
                    // Listing methods resolves their signatures, which fails when a parameter
                    // or return type's assembly is missing.
                    methods.AddRange(type.GetMethods(all));
                    methods.AddRange(type.GetConstructors(all));
                } catch (Exception e) {
                    Record(findings, type.Name, e);
                    continue;
                }
                foreach (MethodBase method in methods) {
                    try {
                        if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) {
                            continue;
                        }
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    } catch (Exception e) {
                        Record(findings, type.Name + "." + method.Name, e);
                    }
                }
            }
            return findings;
        }

        /// <summary>
        /// Record files failure e under its cause.  where names the method that failed, or is
        /// null for a type that failed to load (its message names the type).  A missing member
        /// or type is an OpenSim mismatch when its message names OpenSim; a missing or
        /// mismatched assembly that isn't OpenSim's is a missing assembly.
        /// </summary>
        private static void Record(Findings findings, string where, Exception e) {
            string message = e.Message.TrimEnd('.');
            string entry = where == null ? message : where + ": " + message;
            if (e is FileNotFoundException || e is FileLoadException || e is BadImageFormatException) {
                string assembly = MissingAssemblyName(e);
                if (assembly.StartsWith("OpenSim", StringComparison.Ordinal)) {
                    findings.OpenSimMismatch.Add(entry);
                } else {
                    findings.MissingAssemblies[assembly] = true;
                }
            } else if ((e is MissingMemberException || e is TypeLoadException) &&
                       message.IndexOf("OpenSim", StringComparison.Ordinal) >= 0) {
                // MissingMethodException and MissingFieldException are MissingMemberExceptions
                findings.OpenSimMismatch.Add(entry);
            } else {
                findings.Other.Add(entry);
            }
        }

        /// <summary>
        /// MissingAssemblyName returns the simple name of the assembly a load failure is
        /// about, or its message when the exception doesn't name one.
        /// </summary>
        private static string MissingAssemblyName(Exception e) {
            string fileName = (e as FileNotFoundException)?.FileName ??
                              (e as FileLoadException)?.FileName ??
                              (e as BadImageFormatException)?.FileName;
            if (String.IsNullOrEmpty(fileName)) {
                return e.Message.TrimEnd('.');
            }
            try {
                return new AssemblyName(fileName).Name;
            } catch (Exception) {
                return fileName;
            }
        }
    }
}

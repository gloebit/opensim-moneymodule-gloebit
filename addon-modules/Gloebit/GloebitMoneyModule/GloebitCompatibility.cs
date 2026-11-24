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
        /// MAX_REPORTED caps how many failures the error lists.
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
            public List<string> Errors = new List<string>();   // code in this DLL that can't run on this OpenSim
            public List<string> Warnings = new List<string>(); // version mismatch, or a check that couldn't run
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
        /// a member that was changed or removed is reported here, at startup, instead of when
        /// a user first triggers it.  This also catches builds that share a version label but
        /// differ in API.  It can't catch changed behavior behind an unchanged signature.
        /// All such failures are reported as one error; it doesn't guess at their cause.
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
                List<string> failures = FindFailures();
                if (failures.Count > 0) {
                    report.Errors.Add(String.Format(
                        "{0} part(s) of this Gloebit DLL can't run on this OpenSim.  Gloebit features that use them will fail, and could crash the region, until a matching DLL is installed.  This usually means the DLL was built for a different OpenSim build; download the one matching `show version` from {1}.  First {2}: {3}",
                        failures.Count, RELEASES_URL, Math.Min(failures.Count, MAX_REPORTED),
                        String.Join("; ", failures.GetRange(0, Math.Min(failures.Count, MAX_REPORTED)))));
                }
            } catch (Exception e) {
                report.Warnings.Add(String.Format("Could not check this DLL's references: {0}", e.Message));
            }
            return report;
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
        /// methods and constructors, returning each distinct failure ("Type.Method: message",
        /// or a type-load message) and continuing past it.  Generic and abstract methods are
        /// skipped: they have no single compilable body.
        /// </summary>
        private static List<string> FindFailures() {
            List<string> failures = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            Action<string, Exception> record = (where, e) => {
                // one line per entry: some messages carry newlines
                string message = String.Join(" ", e.Message.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
                string entry = where == null ? message : where + ": " + message;
                if (seen.Add(entry)) {
                    failures.Add(entry);
                }
            };

            Type[] types;
            try {
                types = typeof(GloebitCompatibility).Assembly.GetTypes();
            } catch (ReflectionTypeLoadException e) {
                types = e.Types;
                foreach (Exception loaderException in e.LoaderExceptions) {
                    if (loaderException != null) {
                        record(null, loaderException);
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
                    record(type.Name, e);
                    continue;
                }
                foreach (MethodBase method in methods) {
                    try {
                        if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) {
                            continue;
                        }
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    } catch (Exception e) {
                        record(type.Name + "." + method.Name, e);
                    }
                }
            }
            return failures;
        }
    }
}

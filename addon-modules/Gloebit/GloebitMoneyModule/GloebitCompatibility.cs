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
        /// MAX_MISSING_REPORTED caps how many unresolvable methods Check lists.
        /// </summary>
        public const int MAX_MISSING_REPORTED = 10;

        /// <summary>
        /// RELEASES_URL is where to find a DLL matching the running OpenSim.
        /// </summary>
        public const string RELEASES_URL = "https://github.com/gloebit/opensim-moneymodule-gloebit/releases/latest";

        /// <summary>
        /// CompatibilityReport holds what Check found: Errors for what will fail when used,
        /// Warnings for what might.
        /// </summary>
        public class CompatibilityReport {
            public List<string> Errors = new List<string>();   // methods that can't run on this OpenSim
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
        /// Then it JIT-compiles every method in this DLL with RuntimeHelpers.PrepareMethod.
        /// Compiling a method resolves the OpenSim members it calls, so a member OpenSim
        /// changed or removed is reported here, at startup, instead of when a user first
        /// triggers it.  This also catches builds that share a version label but differ in API.
        /// It can't catch changed behavior behind an unchanged signature.
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
                List<string> loadFailures = new List<string>();
                List<string> missing = FindUnresolvableMethods(loadFailures);
                if (loadFailures.Count > 0) {
                    report.Warnings.Add(String.Format(
                        "{0} type(s) in this Gloebit DLL could not be loaded, so their methods weren't checked: {1}",
                        loadFailures.Count, String.Join("; ", Head(loadFailures))));
                }
                if (missing.Count > 0) {
                    report.Errors.Add(String.Format(
                        "{0} method(s) in this Gloebit DLL call OpenSim code that this OpenSim doesn't have, so they will fail when used.  First {1}: {2}.  This DLL was built for a different OpenSim build; download the one matching `show version` from {3}",
                        missing.Count, Math.Min(missing.Count, MAX_MISSING_REPORTED),
                        String.Join("; ", Head(missing)), RELEASES_URL));
                }
            } catch (Exception e) {
                report.Warnings.Add(String.Format("Could not check this DLL's OpenSim method references: {0}", e.Message));
            }
            return report;
        }

        /// <summary>
        /// Head returns the first MAX_MISSING_REPORTED items of items.
        /// </summary>
        private static List<string> Head(List<string> items) {
            return items.GetRange(0, Math.Min(items.Count, MAX_MISSING_REPORTED));
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
        /// FindUnresolvableMethods JIT-compiles each method and constructor declared in this
        /// assembly and returns "Type.Method: error" for each that fails to compile, e.g. one
        /// that references an OpenSim member or assembly the running OpenSim doesn't have.
        /// Each failure is recorded and the scan continues.  Types that can't be loaded at all
        /// are added to loadFailures.  Generic and abstract methods are skipped: they have no
        /// single compilable body.
        /// </summary>
        private static List<string> FindUnresolvableMethods(List<string> loadFailures) {
            List<string> missing = new List<string>();
            Type[] types;
            try {
                types = typeof(GloebitCompatibility).Assembly.GetTypes();
            } catch (ReflectionTypeLoadException e) {
                types = e.Types;
                foreach (Exception loaderException in e.LoaderExceptions) {
                    if (loaderException != null) {
                        loadFailures.Add(loaderException.Message.TrimEnd('.'));
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
                methods.AddRange(type.GetMethods(all));
                methods.AddRange(type.GetConstructors(all));
                foreach (MethodBase method in methods) {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) {
                        continue;
                    }
                    try {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    } catch (Exception e) {
                        missing.Add(String.Format("{0}.{1}: {2}", type.Name, method.Name, e.Message.TrimEnd('.')));
                    }
                }
            }
            return missing;
        }
    }
}

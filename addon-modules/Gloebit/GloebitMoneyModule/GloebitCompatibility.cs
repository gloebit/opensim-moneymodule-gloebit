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
        /// Check returns warnings about this DLL and the running OpenSim, or an empty list.
        ///
        /// It compares the OpenSim version this DLL was built against with the running one:
        /// VersionInfo.VersionNumber and VERSION_FLAVOUR are constants, so the compiler copied
        /// the built-against values into this DLL, while reading the same fields by reflection
        /// returns the running OpenSim's values.
        ///
        /// Then it JIT-compiles every method in this DLL.  Compiling a method resolves the
        /// OpenSim methods it calls, so a method OpenSim changed or removed fails here, at
        /// startup, instead of when a user first triggers it.  This also catches builds that
        /// share a version label but differ in API.
        ///
        /// Check catches its own exceptions and reports them as warnings.
        /// </summary>
        public static List<string> Check() {
            List<string> warnings = new List<string>();
            try {
                string versionWarning = CheckVersion();
                if (versionWarning != null) {
                    warnings.Add(versionWarning);
                }
            } catch (Exception e) {
                warnings.Add(String.Format("Could not compare OpenSim versions: {0}", e.Message));
            }
            try {
                List<string> missing = FindUnresolvableMethods();
                if (missing.Count > 0) {
                    warnings.Add(String.Format(
                        "{0} method(s) in this Gloebit DLL call OpenSim code that this OpenSim doesn't have, so they will fail when used.  First: {1}.  This DLL was built for a different OpenSim build; download the one matching `show version` from {2}",
                        missing.Count,
                        String.Join("; ", missing.GetRange(0, Math.Min(missing.Count, MAX_MISSING_REPORTED))),
                        RELEASES_URL));
                }
            } catch (Exception e) {
                warnings.Add(String.Format("Could not check this DLL's OpenSim method references: {0}", e.Message));
            }
            return warnings;
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
                "This Gloebit DLL was built for OpenSim {0} {1}, but this is {2}.  Some Gloebit features may fail or crash the region; download the DLL matching `show version` from {3}",
                builtNumber, (OpenSim.VersionInfo.Flavour)builtFlavour,
                OpenSim.VersionInfo.Version.Trim(), RELEASES_URL);
        }

        /// <summary>
        /// FindUnresolvableMethods JIT-compiles each method and constructor declared in this
        /// assembly and returns "Type.Method: error" for each that references an OpenSim
        /// member or type the running OpenSim doesn't have.  Generic and abstract methods are
        /// skipped: they have no single compilable body.
        /// </summary>
        private static List<string> FindUnresolvableMethods() {
            List<string> missing = new List<string>();
            Type[] types;
            try {
                types = typeof(GloebitCompatibility).Assembly.GetTypes();
            } catch (ReflectionTypeLoadException e) {
                types = e.Types;
                foreach (Exception loaderException in e.LoaderExceptions) {
                    if (loaderException != null) {
                        missing.Add(loaderException.Message);
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
                    } catch (Exception e) when (e is MissingMethodException || e is MissingFieldException ||
                                                e is TypeLoadException || e is MissingMemberException) {
                        missing.Add(String.Format("{0}.{1}: {2}", type.Name, method.Name, e.Message.TrimEnd('.')));
                    }
                }
            }
            return missing;
        }
    }
}

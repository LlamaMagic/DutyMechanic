﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace DutyMechanic.Localization {
    using System;


    /// <summary>
    ///   A strongly-typed resource class, for looking up localized strings, etc.
    /// </summary>
    // This class was auto-generated by the StronglyTypedResourceBuilder
    // class via a tool like ResGen or Visual Studio.
    // To add or remove a member, edit your .ResX file then rerun ResGen
    // with the /str option, or rebuild your VS project.
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "17.0.0.0")]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    internal class Translations {

        private static global::System.Resources.ResourceManager resourceMan;

        private static global::System.Globalization.CultureInfo resourceCulture;

        [global::System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal Translations() {
        }

        /// <summary>
        ///   Returns the cached ResourceManager instance used by this class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Resources.ResourceManager ResourceManager {
            get {
                if (object.ReferenceEquals(resourceMan, null)) {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Trust.Localization.Translations", typeof(Translations).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }

        /// <summary>
        ///   Overrides the current thread's CurrentUICulture property for all
        ///   resource lookups using this strongly typed resource class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Globalization.CultureInfo Culture {
            get {
                return resourceCulture;
            }
            set {
                resourceCulture = value;
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Certain jobs may have difficulty with some bosses..
        /// </summary>
        internal static string JOB_DIFFICULTY_WARNING {
            get {
                return ResourceManager.GetString("JOB_DIFFICULTY_WARNING", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Localization changed to &quot;{0}&quot;..
        /// </summary>
        internal static string LOCALIZATION_CHANGED {
            get {
                return ResourceManager.GetString("LOCALIZATION_CHANGED", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Failed to load localization &quot;{0}&quot;: resource file not found..
        /// </summary>
        internal static string LOCALIZATION_NOT_FOUND {
            get {
                return ResourceManager.GetString("LOCALIZATION_NOT_FOUND", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Player has died. Reloading current profile after respawn..
        /// </summary>
        internal static string PLAYER_DIED_RELOADING_PROFILE {
            get {
                return ResourceManager.GetString("PLAYER_DIED_RELOADING_PROFILE", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Player failed to respawn after {0:N0}ms. Is there a special death mechanic?.
        /// </summary>
        internal static string PLAYER_FAILED_TO_RESPAWN {
            get {
                return ResourceManager.GetString("PLAYER_FAILED_TO_RESPAWN", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to https://discord.gg/bmgCq39.
        /// </summary>
        internal static string PROJECT_CHAT_URL {
            get {
                return ResourceManager.GetString("PROJECT_CHAT_URL", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Dungeon logic for Duty Support and Trust dungeons..
        /// </summary>
        internal static string PROJECT_DESCRIPTION {
            get {
                return ResourceManager.GetString("PROJECT_DESCRIPTION", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Trust.
        /// </summary>
        internal static string PROJECT_NAME {
            get {
                return ResourceManager.GetString("PROJECT_NAME", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to https://github.com/LlamaMagic/RBtrust.
        /// </summary>
        internal static string PROJECT_URL {
            get {
                return ResourceManager.GetString("PROJECT_URL", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Sub-zone changed to {0}. Adding specialized avoid definitions for this area..
        /// </summary>
        internal static string SUBZONE_CHANGED_ADDING_AVOIDS {
            get {
                return ResourceManager.GetString("SUBZONE_CHANGED_ADDING_AVOIDS", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to Sub-zone changed to {0}. Clearing previous avoid definitions..
        /// </summary>
        internal static string SUBZONE_CHANGED_CLEARING_AVOIDS {
            get {
                return ResourceManager.GetString("SUBZONE_CHANGED_CLEARING_AVOIDS", resourceCulture);
            }
        }

        /// <summary>
        ///   Looks up a localized string similar to This profile requires the &quot;Trust&quot; plugin to be installed and enabled.  Check your Plugins tab..
        /// </summary>
        internal static string TRUST_PLUGIN_MISSING {
            get {
                return ResourceManager.GetString("TRUST_PLUGIN_MISSING", resourceCulture);
            }
        }
    }
}

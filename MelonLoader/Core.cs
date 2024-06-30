﻿using System;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using MelonLoader.InternalUtils;
using MelonLoader.MonoInternals;
using MelonLoader.Utils;
using System.IO;
using System.Runtime.InteropServices;
using bHapticsLib;
using System.Threading;
using System.Text;
using JNISharp.NativeInterface;

#if NET35
using MelonLoader.CompatibilityLayers;
#endif

#if NET6_0_OR_GREATER
using MelonLoader.CoreClrUtils;
#endif

#pragma warning disable IDE0051 // Prevent the IDE from complaining about private unreferenced methods

namespace MelonLoader
{
	internal static class Core
    {
        private static bool _success = true;

        internal static HarmonyLib.Harmony HarmonyInstance;
        internal static bool Is_ALPHA_PreRelease = false;

        internal static int Initialize()
        {
            var runtimeFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var runtimeDirInfo = new DirectoryInfo(runtimeFolder);
            MelonEnvironment.MelonLoaderDirectory = runtimeDirInfo.Parent!.FullName;
            MelonEnvironment.GameRootDirectory = runtimeDirInfo.Parent!.Parent!.FullName;

            MelonLaunchOptions.Load();
            MelonLogger.Setup();

            IntPtr ptr = BootstrapInterop.NativeGetJavaVM();
            JNI.Initialize(ptr);
            APKAssetManager.Initialize();
            MelonLogger.Msg("Initialized JNI");

#if NET35
            // Disabled for now because of issues
            //Net20Compatibility.TryInstall();
#endif

            MelonUtils.SetupWineCheck();
            Utils.MelonConsole.Init();

            if (MelonUtils.IsUnderWineOrSteamProton())
                Pastel.ConsoleExtensions.Disable();

            Fixes.DotnetLoadFromManagedFolderFix.Install();
            Fixes.UnhandledException.Install(AppDomain.CurrentDomain);
            Fixes.ServerCertificateValidation.Install();
            
            MelonUtils.Setup(AppDomain.CurrentDomain);

            Assertions.LemonAssertMapping.Setup();

            try
            {
                if (!MonoLibrary.Setup()
                    || !MonoResolveManager.Setup())
                {
                    _success = false;
                    return 1;
                }
            }
            catch (SecurityException)
            {
                MelonDebug.Msg("[MonoLibrary] Caught SecurityException, assuming not running under mono and continuing with init");
            }
            catch (MissingMethodException)
            {
                MelonDebug.Msg("[MonoLibrary] Caught MissingMethodException, assuming not running under mono and continuing with init");
            }

#if NET6_0_OR_GREATER
            if (MelonLaunchOptions.Core.UserWantsDebugger && MelonEnvironment.IsDotnetRuntime)
            {
                MelonLogger.Msg("[Init] User requested debugger, attempting to launch now...");
                Debugger.Launch();
            }

            Environment.SetEnvironmentVariable("IL2CPP_INTEROP_DATABASES_LOCATION", MelonEnvironment.Il2CppAssembliesDirectory);
#endif

            HarmonyInstance = new HarmonyLib.Harmony(BuildInfo.Name);
            
#if NET6_0_OR_GREATER
            // if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            //  NativeStackWalk.LogNativeStackTrace();

            Fixes.DotnetAssemblyLoadContextFix.Install();
            Fixes.DotnetModHandlerRedirectionFix.Install();
#endif

            Fixes.ForcedCultureInfo.Install();
            Fixes.InstancePatchFix.Install();
            Fixes.ProcessFix.Install();

#if NET6_0
            Fixes.Il2CppInteropFixes.Install();
#endif

            PatchShield.Install();

            MelonPreferences.Load();

            MelonCompatibilityLayer.LoadModules();

            bHapticsManager.Connect(BuildInfo.Name, UnityInformationHandler.GameName);
            
            MelonHandler.LoadUserlibs(MelonEnvironment.UserLibsDirectory);
            MelonHandler.LoadMelonsFromDirectory<MelonPlugin>(MelonEnvironment.PluginsDirectory);
            MelonEvents.MelonHarmonyEarlyInit.Invoke();
            MelonEvents.OnPreInitialization.Invoke();

            return 0;
        }

        internal static int PreStart()
        {
            MelonEvents.OnApplicationEarlyStart.Invoke();
            return MelonStartScreen.LoadAndRun(PreSetup);
        }

        private static int PreSetup()
        {
            if (_success)
            {
#if NET6_0

                _success = Il2CppAssemblyGenerator.Run();

#else

                MonoModHookGenerator.Run();
#endif
            }
            return _success ? 0 : 1;
        }

        internal static int Start()
        {
            if (!_success)
                return 1;

            MelonEvents.OnPreModsLoaded.Invoke();
            MelonHandler.LoadMelonsFromDirectory<MelonMod>(MelonEnvironment.ModsDirectory);

            MelonEvents.OnPreSupportModule.Invoke();
            if (!SupportModule.Setup())
                return 1;

            AddUnityDebugLog();
            RegisterTypeInIl2Cpp.SetReady();

            MelonEvents.MelonHarmonyInit.Invoke();
            MelonEvents.OnApplicationStart.Invoke();

            return 0;
        }
        
        internal static string GetVersionString()
        {
            var lemon = MelonLaunchOptions.Console.Mode == MelonLaunchOptions.Console.DisplayMode.LEMON;
            var versionStr = $"{(lemon ? "Lemon" : "Melon")}Loader " +
                             $"v{BuildInfo.Version} " +
                             $"{(Is_ALPHA_PreRelease ? "ALPHA Pre-Release" : "Open-Beta")}";
            return versionStr;
        }
        
        internal static void WelcomeMessage()
        {
            //if (MelonDebug.IsEnabled())
            //    MelonLogger.WriteSpacer();

            MelonLogger.MsgDirect("------------------------------");
            MelonLogger.MsgDirect(GetVersionString());
            MelonLogger.MsgDirect($"OS: {MelonUtils.GetOSVersion()}");
            MelonLogger.MsgDirect($"Hash Code: {MelonUtils.HashCode}");
            MelonLogger.MsgDirect("------------------------------");
            var typeString = MelonUtils.IsGameIl2Cpp() ? "Il2cpp" : MelonUtils.IsOldMono() ? "Mono" : "MonoBleedingEdge";
            MelonLogger.MsgDirect($"Game Type: {typeString}");
            var archString = MelonUtils.IsGame32Bit() ? "x86" : "x64";
            MelonLogger.MsgDirect($"Game Arch: {archString}");
            MelonLogger.MsgDirect("------------------------------");

            MelonEnvironment.PrintEnvironment();
        }
        
        internal static void Quit()
        {
            MelonDebug.Msg("[ML Core] Received Quit from Support Module. Shutting down...");
            
            MelonPreferences.Save();

            HarmonyInstance.UnpatchSelf();
            bHapticsManager.Disconnect();

            MelonLogger.Flush();
            //MelonLogger.Close();
            
            Thread.Sleep(200);

            if (MelonLaunchOptions.Core.QuitFix)
                Process.GetCurrentProcess().Kill();
        }

        private static void AddUnityDebugLog()
        {
            var msg = "~   This Game has been MODIFIED using MelonLoader. DO NOT report any issues to the Game Developers!   ~";
            var line = new string('-', msg.Length);
            SupportModule.Interface.UnityDebugLog(line);
            SupportModule.Interface.UnityDebugLog(msg);
            SupportModule.Interface.UnityDebugLog(line);
        }
    }
}
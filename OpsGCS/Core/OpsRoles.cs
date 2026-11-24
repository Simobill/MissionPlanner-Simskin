using MissionPlanner.Utilities;

namespace MissionPlanner.OpsGCS.Core
{
    public enum OpsRole
    {
        Operator,
        Engineer
    }

    /// <summary>
    /// Display presets per role. Operator = fly, plan, monitor. Engineer = stock Advanced view.
    /// Built on upstream DisplayView flags so upstream UI code does the actual hiding.
    /// </summary>
    public static class OpsDisplayViews
    {
        public static DisplayView For(OpsRole role)
        {
            return role == OpsRole.Engineer ? new DisplayView().Advanced() : Operator();
        }

        public static DisplayView Operator()
        {
            var v = new DisplayView().Basic();

            v.displayName = DisplayNames.Custom;

            // main menu
            v.displaySimulation = false;
            v.displayDonate = false;
            v.displayHelp = false;
            v.displayTerminal = false;

            // flight data tabs: keep what an operator needs in flight
            v.displayQuickTab = true;
            v.displayPreFlightTab = true;
            v.displayPreFlightTabEdit = false;
            v.displaySimpleActionsTab = true;
            v.displayAdvActionsTab = false;
            v.displayGaugesTab = false;
            v.displayStatusTab = false;
            v.displayServoTab = false;
            v.displayScriptsTab = false;
            v.displayTelemetryTab = true;
            v.displayDataflashTab = false;
            v.displayMessagesTab = true;
            v.displayTransponderTab = false;
            v.displayAuxFunctionTab = false;
            v.displayPayloadTab = true;
            v.displayAnenometer = true;
            v.lockQuickView = true;

            // flight planner: waypoints, survey, fence, rally; no scripting / tracker extras
            v.displayRallyPointsMenu = true;
            v.displayGeoFenceMenu = true;
            v.displayCircleSurveyAutoWp = true;
            v.displaySplineCircleAutoWp = true;
            v.displayTextAutoWp = false;
            v.displayPoiMenu = true;
            v.displayTrackerHomeMenu = false;
            v.displayCheckHeightBox = true;
            v.displayPluginAutoWp = false;
            v.displayPlannerSettings = false;
            v.displayPlannerLayout = false;

            // setup / config pages (also hidden at menu level by the plugin)
            v.displayInstallFirmware = false;
            v.displayWizard = false;
            v.displayFrameType = false;
            v.displayAccelCalibration = false;
            v.displayCompassConfiguration = false;
            v.displayRadioCalibration = false;
            v.displayServoOutput = false;
            v.displayEscCalibration = false;
            v.displayFlightModes = false;
            v.displayFailSafe = false;
            v.displaySikRadio = false;
            v.displayBattMonitor = false;
            v.displayCAN = false;
            v.displayCompassMotorCalib = false;
            v.displayRangeFinder = false;
            v.displayAirSpeed = false;
            v.displayPx4Flow = false;
            v.displayOpticalFlow = false;
            v.displayOsd = false;
            v.displayOSD = false;
            v.displayCameraGimbal = false;
            v.displayMotorTest = false;
            v.displayBluetooth = false;
            v.displayParachute = false;
            v.displayEsp = false;
            v.displayAntennaTracker = false;
            v.displaySerialPorts = false;
            v.displayHWIDs = false;
            v.displayGPSOrder = false;
            v.displayFFTSetup = false;
            v.displayUserParam = false;
            v.displayJoystick = false;
            v.displayREPL = false;
            v.displayRTKInject = true;
            v.displayADSB = true;

            v.displayGeoFence = false;
            v.displayBasicTuning = false;
            v.displayExtendedTuning = false;
            v.displayStandardParams = false;
            v.displayAdvancedParams = false;
            v.displayMavFTP = false;
            v.displayFullParamList = false;
            v.displayFullParamTree = false;
            v.displayParamCommitButton = false;
            v.displayInitialParams = false;

            // connection bar: keep port + baud, standard modes only
            v.displayBaudCMB = true;
            v.displaySerialPortCMB = true;
            v.standardFlightModesOnly = true;

            v.autoHideMenuForce = false;
            v.isAdvancedMode = false;

            return v;
        }
    }
}

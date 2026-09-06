namespace DesktopAgent.Core.Protocol;

public static class GroundingGuidance
{
    public const string ForCoordinates = """
Before locating a control, identify its complete visible label and the row or group it belongs to. Follow that same row or group to the control; check the neighboring labels to avoid choosing a similar control above or below it. Locate the CENTER of the control itself, not the label or a neighboring control.
Use the FIRST image as the coordinate reference, including its margins. Normalize x by the FIRST image's width and y independently by its height: x = round(1000 * horizontal_pixel / (width - 1)); y = round(1000 * vertical_pixel / (height - 1)). Do not use width for both axes, return raw pixel positions, or measure against a second context image. Before returning, check that the point lies inside the intended control on the identified row or group. Return only the requested JSON, without these checks or intermediate calculations.
""";
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace PwrDrvr.LambdaDispatch.Router;

public class CrossPlatformProcessTime
{
  [DllImport("libc", SetLastError = true)]
  private static extern int getrusage(int who, ref RUsage usage);

  [DllImport("libc", SetLastError = true)]
  private static extern int mach_timebase_info(ref MachTimebaseInfo info);

  private const int RUSAGE_SELF = 0;

  [StructLayout(LayoutKind.Sequential)]
  private struct TimeValue
  {
    public long Seconds;
    public int MicroSeconds;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct RUsage
  {
    public TimeValue UserTime;
    public TimeValue SystemTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)]
    public long[] ru_opaque;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MachTimebaseInfo
  {
    public uint Numer;
    public uint Denom;
  }

  public static double GetTotalProcessorTimeMilliseconds()
  {
    if (OperatingSystem.IsMacOS())
    {
      return GetTotalProcessorTimeMillisecondsMacOS();
    }
    else
    {
      return Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
    }
  }

  [SupportedOSPlatform("macos")]
  private static double GetTotalProcessorTimeMillisecondsMacOS()
  {
    var usage = new RUsage();
    if (getrusage(RUSAGE_SELF, ref usage) != 0)
    {
      throw new InvalidOperationException("getrusage failed");
    }

    var userTime = usage.UserTime.Seconds + usage.UserTime.MicroSeconds / 1e6;
    var systemTime = usage.SystemTime.Seconds + usage.SystemTime.MicroSeconds / 1e6;

    var totalProcessorTime = (userTime + systemTime);

    return totalProcessorTime * 1000; // Convert to milliseconds
  }

  // Definitions for RUsage, TimeValue, MachTimebaseInfo, getrusage, and mach_timebase_info go here...
}
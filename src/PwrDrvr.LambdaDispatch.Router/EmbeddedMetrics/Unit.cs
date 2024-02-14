using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Unit
{
  [EnumMember(Value = "Seconds")]
  Seconds,

  [EnumMember(Value = "Microseconds")]
  Microseconds,

  [EnumMember(Value = "Milliseconds")]
  Milliseconds,

  [EnumMember(Value = "Bytes")]
  Bytes,

  [EnumMember(Value = "Kilobytes")]
  Kilobytes,

  [EnumMember(Value = "Megabytes")]
  Megabytes,

  [EnumMember(Value = "Gigabytes")]
  Gigabytes,

  [EnumMember(Value = "Terabytes")]
  Terabytes,

  [EnumMember(Value = "Bits")]
  Bits,

  [EnumMember(Value = "Kilobits")]
  Kilobits,

  [EnumMember(Value = "Megabits")]
  Megabits,

  [EnumMember(Value = "Gigabits")]
  Gigabits,

  [EnumMember(Value = "Terabits")]
  Terabits,

  [EnumMember(Value = "Percent")]
  Percent,

  [EnumMember(Value = "Count")]
  Count,

  [EnumMember(Value = "Bytes/Second")]
  BytesPerSecond,

  [EnumMember(Value = "Kilobytes/Second")]
  KilobytesPerSecond,

  [EnumMember(Value = "Megabytes/Second")]
  MegabytesPerSecond,

  [EnumMember(Value = "Gigabytes/Second")]
  GigabytesPerSecond,

  [EnumMember(Value = "Terabytes/Second")]
  TerabytesPerSecond,

  [EnumMember(Value = "Bits/Second")]
  BitsPerSecond,

  [EnumMember(Value = "Kilobits/Second")]
  KilobitsPerSecond,

  [EnumMember(Value = "Megabits/Second")]
  MegabitsPerSecond,

  [EnumMember(Value = "Gigabits/Second")]
  GigabitsPerSecond,

  [EnumMember(Value = "Terabits/Second")]
  TerabitsPerSecond,

  [EnumMember(Value = "Count/Second")]
  CountPerSecond,

  [EnumMember(Value = "None")]
  None
}
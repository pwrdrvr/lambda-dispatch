public class TrailingAverage
{
  private readonly List<(DateTime timestamp, double sum, int count)> _data = [];
  private double _totalSum = 0;
  private int _totalCount = 0;

  private void CleanupOldData()
  {
    var now = DateTime.UtcNow;
    var currentSecond = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

    // Remove items that are older than 5 seconds from the end of the list
    while (_data.Count > 0 && (currentSecond - _data[^1].timestamp).TotalSeconds > 5)
    {
      var oldest = _data[^1];
      _totalSum -= oldest.sum;
      _totalCount -= oldest.count;
      _data.RemoveAt(_data.Count - 1);
    }
  }

  public void Add(double value)
  {
    CleanupOldData();

    var now = DateTime.UtcNow;
    var currentSecond = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

    // If the list is empty or the current second is different from the first second in the list,
    // insert a new item at the beginning of the list
    if (_data.Count == 0 || _data[0].timestamp != currentSecond)
    {
      _data.Insert(0, (currentSecond, value, 1));
    }
    else
    {
      // Otherwise, update the sum and count for the current second
      var first = _data[0];
      _data[0] = (first.timestamp, first.sum + value, first.count + 1);
    }

    _totalSum += value;
    _totalCount++;
  }

  public double Average
  {
    get
    {
      CleanupOldData();

      // We are not interested in mathematical correctness... if there are no stats, return 0
      if (_totalCount == 0)
      {
        return 0;
      }

      return _totalSum / _totalCount;
    }
  }
}
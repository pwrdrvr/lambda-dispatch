// Only on linux
#[cfg(target_os = "linux")]
pub fn get_threads() -> u64 {
  return match procfs::process::Process::myself() {
    Ok(proc) => match proc.stat() {
      Ok(stat) => stat.num_threads.try_into().unwrap_or(0),
      _ => 0,
    },
    _ => 0,
  };
}

// Only on Mac
#[cfg(target_os = "macos")]
pub fn get_threads() -> u64 {
  let thread_count = match std::process::Command::new("ps")
    .arg("-M")
    .arg(format!("{}", std::process::id()))
    .output()
  {
    Ok(output) => {
      // count the number of lines of output and subtract 1
      // to account for the header line
      let thread_count = std::cmp::max(
        String::from_utf8(output.stdout)
          .unwrap_or_else(|_| String::from(""))
          .lines()
          .count() as u64
          - 1,
        0,
      );
      thread_count
    }
    _ => 0,
  };

  return thread_count as u64;
}

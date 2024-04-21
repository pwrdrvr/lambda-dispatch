const lcovParse = require("lcov-parse");
const path = require("path");

const lcovPath = process.argv[2];
const needsImprovementBelow = parseFloat(
  process.argv.length >= 4 ? process.argv[3] : "90"
);
const poorBelow = parseFloat(process.argv[4] >= 5 ? process.argv[4] : "50");

if (!lcovPath || isNaN(needsImprovementBelow) || isNaN(poorBelow)) {
  console.error(
    "Please provide the path to the lcov.info file and the 'needs-improvement-below' and 'poor-below' percentages as command-line arguments."
  );
  process.exit(1);
}

const outputFormat = "markdown";

if (outputFormat === "markdown") {
  console.log("| File | Line Coverage | Branch |");
  console.log("| --- | --- | --- |");
}

lcovParse(lcovPath, function (err, data) {
  if (err) {
    console.error(err);
  } else {
    data.forEach((file) => {
      const relativePath = file.file.replace(process.cwd(), "");
      const lineCoverage = ((file.lines.hit / file.lines.found) * 100).toFixed(
        1
      );
      const branchCoverage = (
        (file.branches.hit / file.branches.found) *
        100
      ).toFixed(1);
      let emoji;
      if (lineCoverage >= needsImprovementBelow) {
        emoji = "✅"; // white-check emoji
      } else if (
        lineCoverage < needsImprovementBelow &&
        lineCoverage >= poorBelow
      ) {
        emoji = "🟡"; // yellow-ball emoji
      } else {
        emoji = "❌"; // red-x emoji
      }
      if (outputFormat === "markdown") {
        console.log(
          `| ${relativePath} | ${emoji} ${lineCoverage}% | ${
            file.branches.found === 0 ? "-" : `${branchCoverage}%`
          } |`
        );
      } else {
        console.log(
          `${emoji} File: ${relativePath}, Line Coverage: ${lineCoverage}%, Branch Coverage: ${branchCoverage}%`
        );
      }
    });
  }
});

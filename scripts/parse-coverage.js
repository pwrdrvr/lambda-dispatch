const lcovParse = require("lcov-parse");
const fs = require("fs");

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

if (!fs.existsSync(lcovPath)) {
  console.error(
    `The file ${lcovPath} does not exist. Please provide the path to the lcov.info file.`
  );
  process.exit(1);
}

const outputFormat = "markdown";

if (outputFormat === "markdown") {
  console.log(
    "| File | Lines | Lines Hit / Found | Uncovered Lines | Branches |"
  );
  console.log("| --- | --- | --- | --- | --- |");
}

function shortenPath(path, maxLength) {
  if (path.length <= maxLength) {
    return path;
  }

  const start = path.substring(0, maxLength / 2 - 2); // -2 for the '..' in the middle
  const end = path.substring(path.length - maxLength / 2, path.length);

  return `${start}..${end}`;
}

function getEmoji(lineCoverage, needsImprovementBelow, poorBelow) {
  if (lineCoverage >= needsImprovementBelow) {
    return "✅"; // white-check emoji
  } else if (
    lineCoverage < needsImprovementBelow &&
    lineCoverage >= poorBelow
  ) {
    return "🟡"; // yellow-ball emoji
  } else {
    return "❌"; // red-x emoji
  }
}

lcovParse(lcovPath, function (err, data) {
  if (err) {
    console.error(err);
  } else {
    let totalLinesHit = 0;
    let totalLinesFound = 0;
    let totalBranchesHit = 0;
    let totalBranchesFound = 0;

    data.forEach((file) => {
      totalLinesHit += file.lines.hit;
      totalLinesFound += file.lines.found;
      totalBranchesHit += file.branches.hit;
      totalBranchesFound += file.branches.found;
      const relativePath = shortenPath(
        file.file.replace(process.cwd(), ""),
        50
      );
      const lineCoverage = ((file.lines.hit / file.lines.found) * 100).toFixed(
        1
      );
      const branchCoverage = (
        (file.branches.hit / file.branches.found) *
        100
      ).toFixed(1);
      let emoji = getEmoji(lineCoverage, needsImprovementBelow, poorBelow);
      if (outputFormat === "markdown") {
        console.log(
          `| ${relativePath} | ${emoji}&nbsp;${lineCoverage}% | ${
            file.lines.hit
          }&nbsp;/&nbsp;${file.lines.found} | ${
            file.lines.found - file.lines.hit
          } | ${file.branches.found === 0 ? "-" : `${branchCoverage}%`} |`
        );
      } else {
        console.log(
          `${emoji} File: ${relativePath}, Line Coverage: ${lineCoverage}%, Branch Coverage: ${branchCoverage}%`
        );
      }
    });

    const overallLineCoverage = (
      (totalLinesHit / totalLinesFound) *
      100
    ).toFixed(1);
    const overallBranchCoverage = (
      (totalBranchesHit / totalBranchesFound) *
      100
    ).toFixed(1);
    const totalUncoveredLines = totalLinesFound - totalLinesHit;
    const overallEmoji = getEmoji(
      overallLineCoverage,
      needsImprovementBelow,
      poorBelow
    );

    if (outputFormat === "markdown") {
      console.log(
        `| Overall | ${overallEmoji}&nbsp;${overallLineCoverage}% | ${totalLinesHit}&nbsp;/&nbsp;${totalLinesFound} | ${totalUncoveredLines} | ${
          totalBranchesFound === 0 ? "-" : `${overallBranchCoverage}%`
        } |`
      );
    } else {
      console.log(
        `Overall Line Coverage: ${overallLineCoverage}%, Overall Branch Coverage: ${overallBranchCoverage}%`
      );
    }
  }
});

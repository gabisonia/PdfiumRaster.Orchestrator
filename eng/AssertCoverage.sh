#!/usr/bin/env bash

set -euo pipefail

coverage_directory="${1:?Usage: AssertCoverage.sh <coverage-directory> [minimum-line-percent] [minimum-branch-percent]}"
minimum_line_percent="${2:-90}"
minimum_branch_percent="${3:-80}"
coverage_file="$(find "$coverage_directory" -name coverage.cobertura.xml -type f -print | sort | tail -n 1)"

if [[ -z "$coverage_file" ]]; then
    echo "No coverage.cobertura.xml file was found under $coverage_directory." >&2
    exit 1
fi

coverage_element="$(grep -m 1 '<coverage ' "$coverage_file")"
line_rate="$(sed -n 's/.*line-rate="\([^"]*\)".*/\1/p' <<<"$coverage_element")"
branch_rate="$(sed -n 's/.*branch-rate="\([^"]*\)".*/\1/p' <<<"$coverage_element")"

if [[ -z "$line_rate" || -z "$branch_rate" ]]; then
    echo "Could not read line-rate and branch-rate from $coverage_file." >&2
    exit 1
fi

line_percent="$(awk -v rate="$line_rate" 'BEGIN { printf "%.2f", rate * 100 }')"
branch_percent="$(awk -v rate="$branch_rate" 'BEGIN { printf "%.2f", rate * 100 }')"

echo "Coverage: ${line_percent}% lines, ${branch_percent}% branches."

awk -v actual="$line_percent" -v minimum="$minimum_line_percent" \
    'BEGIN { if (actual + 0 < minimum + 0) exit 1 }' || {
    echo "Line coverage ${line_percent}% is below the required ${minimum_line_percent}%." >&2
    exit 1
}

awk -v actual="$branch_percent" -v minimum="$minimum_branch_percent" \
    'BEGIN { if (actual + 0 < minimum + 0) exit 1 }' || {
    echo "Branch coverage ${branch_percent}% is below the required ${minimum_branch_percent}%." >&2
    exit 1
}

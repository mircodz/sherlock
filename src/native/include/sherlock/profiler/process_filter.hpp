#pragma once

#include <span>
#include <string>
#include <string_view>

namespace Sherlock::process_filter {

struct Selection {
    bool included;
    std::string name;
    std::string error;
};

// Newline-separated filename globs: * and ?, ASCII case-insensitive; no paths or blank entries.
// Reads at most 1 MiB of command-line data. Read/validation failures never include a process.
Selection current(std::string_view patterns);

// Arguments include argv[0]. A supplied executable path takes precedence over argv[0].
// dotnet SDK commands retain host identity; direct managed entries use their basename.
// Other hosts match their executable basename or conventional <executable-stem>.dll alias.
// Renamed/custom hosts cannot reveal arbitrary hosted assemblies through this selector.
// name is the matched basename/alias, or the entry/executable basename when excluded.
Selection select(std::span<const std::string> arguments, std::string_view executable, std::string_view patterns);

} // namespace Sherlock::process_filter

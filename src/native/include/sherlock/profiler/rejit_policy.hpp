#pragma once

#include <string_view>

#include "profilercommon.h"

namespace Sherlock::rejit {

// A null reason permits instrumentation. Check before requesting ReJIT, not just before replacing IL.
const char* rejection(std::string_view assembly, std::string_view type, std::string_view method);
const char* rejection(IMetaDataImport* metadata, mdMethodDef method);
const char* rejection(ICorProfilerInfo10* info, ModuleID module, mdMethodDef method);

} // namespace Sherlock::rejit

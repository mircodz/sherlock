#pragma once

#include <string_view>

#include "profilercommon.h"

namespace Sherlock::rejit {

// A null reason permits instrumentation. Check before requesting ReJIT, not just before replacing IL.
// By name: CoreLib methods the runtime owns (array entry helpers, JIT helpers).
const char* rejection(std::string_view assembly, std::string_view type, std::string_view method);
// By implementation flags: methods whose IL doesn't match their signature (runtime async).
const char* rejection(DWORD implementationFlags);
const char* rejection(IMetaDataImport* metadata, mdMethodDef method);
const char* rejection(ICorProfilerInfo10* info, ModuleID module, mdMethodDef method);

} // namespace Sherlock::rejit

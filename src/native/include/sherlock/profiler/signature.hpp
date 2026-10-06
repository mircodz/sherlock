#pragma once

#include <functional>
#include <optional>
#include <span>
#include <string>
#include <string_view>

#include "profilercommon.h"

namespace Sherlock::signature {

struct Context {
    // Metadata name of a TypeDef or TypeRef token, e.g. "System.Collections.Generic.List`1".
    std::function<std::string(mdToken)> typeName;
    std::function<std::span<const BYTE>(mdTypeSpec)> typeSpec;
    std::span<const std::string> typeParameters;
    std::span<const std::string> methodParameters;
};

// "System.Collections.Generic.List`1" -> "List".
std::string shortTypeName(std::string_view metadataName);

// Formats a MethodDefSig as a C#-style parameter list such as "(int, List<string>, ref T)", with
// ":<return type>" appended when requested. Returns nullopt for malformed or oversized signatures.
std::optional<std::string> format(std::span<const BYTE> methodSignature, const Context& context, bool includeReturnType);

} // namespace Sherlock::signature

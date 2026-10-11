#pragma once

#include <cstddef>
#include <functional>
#include <optional>
#include <span>
#include <string>
#include <string_view>

#include "profilercommon.h"

namespace Sherlock::signature {

struct Context {
    // Display name of a TypeDef or TypeRef token without generic arity: "List" or, when qualified,
    // "System.Collections.Generic.List" (enclosing types included for nested types).
    std::function<std::string(mdToken, bool qualified)> typeName;
    std::function<std::span<const BYTE>(mdTypeSpec)> typeSpec;
    std::span<const std::string> typeParameters;
    std::span<const std::string> methodParameters;
    // How a byref parameter of the described method was declared: "out", "in", or "ref readonly"; empty for "ref".
    // By sequence: 0 is the return value, 1 the first parameter. Signatures alone spell all of them "ref".
    std::function<std::string(std::uint32_t sequence)> byrefKeyword;
};

// "System.Collections.Generic.List`1" -> "List".
std::string shortTypeName(std::string_view metadataName);

// "List`1" -> "List"; names without a numeric arity marker are unchanged.
std::string withoutArity(std::string_view metadataName);

// Spells a constructed type the way ClrMD names heap objects, so profiler and heap type names can be joined:
// segments {"System.Collections.Generic.Dictionary`2", "Entry"} (outermost first) with arguments
// {"System.String", "System.Object"} -> "System.Collections.Generic.Dictionary<System.String, System.Object>+Entry".
// Each segment takes as many arguments as its own arity marker. Returns nullopt when they don't add up.
std::optional<std::string> constructedTypeName(std::span<const std::string> segments, std::span<const std::string> typeArguments);

struct Signature {
    std::string parameters; // "(int, List<string>, ref T)"
    std::string returnType; // "void"
};

// Formats a MethodDefSig in C# style. Qualified spells type names with their namespaces
// ("System.Collections.Generic.List<string>"). Returns nullopt for malformed or oversized signatures.
std::optional<Signature> describe(std::span<const BYTE> methodSignature, const Context& context, bool qualified);

} // namespace Sherlock::signature

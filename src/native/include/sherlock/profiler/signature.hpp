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
};

struct Detail {
    bool qualifiedTypes = false;
    bool returnType = false;
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

// Formats a MethodDefSig as a C#-style parameter list such as "(int, List<string>, ref T)", with
// ":<return type>" appended when requested. Returns nullopt for malformed or oversized signatures.
std::optional<std::string> format(std::span<const BYTE> methodSignature, const Context& context, Detail detail);

// Frames are interned by label, so a method's label must differ from every same-named method on its type.
// Returns the least detailed label of method `self` that does: short types, qualified types, short types
// with the return type, then both. `label(i, detail)` formats method i of `count`, or nullopt when it can't.
// Returns nullopt when `self` can't be formatted or even its most detailed label is shared.
std::optional<std::string> distinctLabel(
    std::size_t self, std::size_t count, const std::function<std::optional<std::string>(std::size_t, Detail)>& label);

} // namespace Sherlock::signature

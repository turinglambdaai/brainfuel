#!/usr/bin/env node
// Generate linux/src/GeneratedStrings.hpp from the shared/i18n single source
// (zh.json / en.json). Run from the repo root:
//   node scripts/gen-cpp-strings.mjs
import { readFileSync, writeFileSync } from "node:fs";

const zh = JSON.parse(readFileSync("shared/i18n/zh.json", "utf8"));
const en = JSON.parse(readFileSync("shared/i18n/en.json", "utf8"));

const esc = (s) =>
  String(s)
    .replace(/\\/g, "\\\\")
    .replace(/"/g, '\\"')
    .replace(/\n/g, "\\n")
    .replace(/\r/g, "\\r")
    .replace(/\t/g, "\\t");
const table = (obj) =>
  Object.entries(obj)
    .map(([k, v]) => `    {"${esc(k)}", "${esc(String(v))}"},`)
    .join("\n");

const out = `// Generated from shared/i18n/{zh,en}.json — the single i18n source.
// Do not edit; run: node scripts/gen-cpp-strings.mjs
#pragma once

#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace l10n {

using Table = std::unordered_map<std::string, std::string>;

// Table storage lives in a function-local static so headers stay includable
// from multiple translation units without ODR trouble.
inline Table const& zh_table() {
  static Table const t = {
${table(zh)}
  };
  return t;
}

inline Table const& en_table() {
  static Table const t = {
${table(en)}
  };
  return t;
}

inline std::string& language() {
  static std::string lang = "zh";
  return lang;
}

// Look up a key in the active language, falling back to zh then the key
// itself. "{0}"-style placeholders are substituted from the args.
inline std::string t(std::string const& key,
                     std::vector<std::string> const& args = {}) {
  Table const& active = language() == "en" ? en_table() : zh_table();
  auto it = active.find(key);
  if (it == active.end()) {
    it = zh_table().find(key);
  }
  std::string s = it == active.end() ? key : it->second;
  for (std::size_t i = 0; i < args.size(); ++i) {
    std::string const placeholder = "{" + std::to_string(i) + "}";
    for (std::size_t pos = s.find(placeholder); pos != std::string::npos;
         pos = s.find(placeholder, pos + args[i].size())) {
      s.replace(pos, placeholder.size(), args[i]);
    }
  }
  return s;
}

}  // namespace l10n
`;

writeFileSync("linux/src/GeneratedStrings.hpp", out);
console.log(`GeneratedStrings.hpp: ${Object.keys(zh).length} zh / ${Object.keys(en).length} en keys`);

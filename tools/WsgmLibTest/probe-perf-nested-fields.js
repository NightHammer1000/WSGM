// Probe: the field names on the nested perf limits/settings messages, which are not top-level
// exports of the protobuf module, so the shim fills the fields Valve's own controls read.
//
// Reads ONE named module's factory as a string and parses the generated field metadata out of it.
// Nothing is resolved by loop, nothing is constructed, nothing is written. The earlier version of
// this probe searched the bundle by instantiating every export it could reach and signed the
// developer's Steam out; see the rule in docs\steam-cef.md.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_nested_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);

  const out = {};
  try {
    const source = String(runtime.m["28013"]);

    // Each generated class carries `fields:{name:{n:tag,...},...}` inside its static metadata and
    // declares its name in getClassName(). Taking the last fields block before the name lands on
    // that class's own metadata.
    const fieldsAt = [];
    for (let at = source.indexOf("fields:{"); at >= 0; at = source.indexOf("fields:{", at + 1)) {
      fieldsAt.push(at);
    }

    const names = (start) => {
      const keys = [];
      let depth = 0;
      let token = "";
      for (let i = start + "fields:".length; i < source.length; i++) {
        const ch = source[i];
        if (ch === "{") {
          depth++;
          if (depth === 1) token = "";
          continue;
        }
        if (ch === "}") {
          depth--;
          if (depth === 0) break;
          continue;
        }
        if (depth !== 1) continue;
        if (ch === ":") {
          if (token.trim()) keys.push(token.trim());
          token = "";
        } else if (ch === ",") {
          token = "";
        } else {
          token += ch;
        }
      }
      return keys;
    };

    for (const name of [
      "CMsgSystemPerfLimits",
      "CMsgSystemPerfSettingsGlobal",
      "CMsgSystemPerfSettingsPerApp",
    ]) {
      const declaredAt = source.indexOf('return"' + name + '"');
      if (declaredAt < 0) {
        out[name] = null;
        continue;
      }
      const owning = fieldsAt.filter((at) => at < declaredAt).pop();
      out[name] = owning === undefined ? null : names(owning);
    }
  } catch (error) {
    out.error = String(error);
  }

  return JSON.stringify(out);
})();

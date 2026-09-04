const fs = require("fs");
const path = require("path");

const manifestPath = path.join(__dirname, "manifest.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));

function fail(message) {
  process.stderr.write(message + "\n");
  process.exit(1);
}

if (manifest.manifest_version !== 3) {
  fail("manifest_version must be 3");
}
if (!Array.isArray(manifest.permissions) || manifest.permissions.indexOf("nativeMessaging") < 0) {
  fail("nativeMessaging permission is required");
}
if (manifest.permissions.indexOf("cookies") >= 0) {
  fail("cookies permission is not allowed");
}
if (manifest.permissions.indexOf("scripting") < 0) {
  fail("scripting permission is required for page-context ChatGPT fetches");
}
if (manifest.permissions.indexOf("tabs") >= 0) {
  fail("tabs permission is not required; host access is enough to query chatgpt.com tabs");
}
if (!Array.isArray(manifest.host_permissions) || manifest.host_permissions.length !== 1 || manifest.host_permissions[0] !== "https://chatgpt.com/*") {
  fail("host_permissions must be exactly https://chatgpt.com/*");
}
process.stdout.write("extension manifest ok\n");

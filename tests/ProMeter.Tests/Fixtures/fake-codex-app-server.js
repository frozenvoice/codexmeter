const readline = require("readline");

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

function write(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n", "utf8");
}

rl.on("line", (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }

  if (message.method === "initialize") {
    write({ id: message.id, result: { protocolVersion: "1" } });
    return;
  }

  if (message.method === "initialized") {
    write({ method: "session/ready", params: {} });
    return;
  }

  if (message.method === "account/read") {
    write({ id: message.id, result: { loggedIn: true, account: { planType: "plus" } } });
    return;
  }

  if (message.method === "account/rateLimits/read") {
    write({
      id: message.id,
      result: {
        rateLimits: {
          primary: { usedPercent: 42, windowDurationMins: 300, resetsAt: 1893456000 },
          secondary: { usedPercent: 31, windowDurationMins: 10080, resetsAt: 1894051200 },
          ordinaryUsageAllowed: true,
          rateLimitResetCredits: { availableCount: 1 }
        }
      }
    });
  }
});

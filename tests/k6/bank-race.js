/**
 * k6 Bank Race Condition Test
 *
 * Simulates 10 users submitting bank transactions simultaneously via SignalR.
 * Expected result:
 *   - Exactly 1 response with status = "accepted"
 *   - 9 responses with status = "rejected" (bank busy)
 *
 * Usage (inside workspace container):
 *   k6 run tests/k6/bank-race.js
 *
 * With custom backend URL:
 *   k6 run --env BASE_URL=http://workspace:5000 tests/k6/bank-race.js
 */

import ws from "k6/ws";
import http from "k6/http";
import { check } from "k6";
import { Counter } from "k6/metrics";

const accepted = new Counter("transactions_accepted");
const rejected = new Counter("transactions_rejected");

// 10 VUs, each runs exactly 1 iteration — all start simultaneously
export const options = {
  scenarios: {
    race: {
      executor: "per-vu-iterations",
      vus: 50,
      iterations: 1,
      maxDuration: "30s",
    },
  },
  thresholds: {
    // Every VU must get a valid response
    checks: ["rate==1.0"],
    // At most 1 transaction accepted (global bank lock enforces this)
    transactions_accepted: ["count<=1"],
  },
};

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";
const WS_URL = BASE_URL.replace(/^http/, "ws");
const RS = "\x1e"; // SignalR record separator (ASCII 30)

// ── Helpers ──────────────────────────────────────────────────────────────────

function getToken(tenantId, userName) {
  const res = http.post(
    `${BASE_URL}/api/auth/token`,
    JSON.stringify({ tenantId, clientType: "ui", userName }),
    { headers: { "Content-Type": "application/json" } },
  );
  if (res.status !== 200) {
    throw new Error(`[VU ${__VU}] Auth failed: ${res.status} ${res.body}`);
  }
  return JSON.parse(res.body).accessToken;
}

// ── Main test function ────────────────────────────────────────────────────────

export default function () {
  const tenantId = "TenantA";
  const userName = `loadtest-vu${__VU}`;
  const token = getToken(tenantId, userName);

  let txResult = null;

  ws.connect(
    `${WS_URL}/hubs/aura?access_token=${token}`,
    {},
    function (socket) {
      socket.on("open", () => {
        // Step 1: SignalR handshake
        socket.send(JSON.stringify({ protocol: "json", version: 1 }) + RS);
      });

      socket.on("message", (data) => {
        const frames = data.split(RS).filter((f) => f.trim().length > 0);

        for (const frame of frames) {
          let msg;
          try {
            msg = JSON.parse(frame);
          } catch {
            continue;
          }

          // type undefined + empty object → handshake ack → send transaction
          if (msg.type === undefined) {
            socket.send(
              JSON.stringify({
                type: 1,
                invocationId: "1",
                target: "SubmitTransaction",
                arguments: [
                  {
                    description: `race-test-vu${__VU}`,
                    amount: 1000,
                    currency: "VND",
                  },
                ],
              }) + RS,
            );
          }

          // type 3 → method completion (result or error)
          if (msg.type === 3 && msg.invocationId === "1") {
            txResult = msg.result;
            socket.close();
          }

          // type 6 → server ping, ignore
        }
      });

      // Safety timeout — should never hit in normal conditions
      socket.setTimeout(() => socket.close(), 15000);
    },
  );

  // ── Assertions ─────────────────────────────────────────────────────────────
  const ok = check(txResult, {
    "received a response": (r) => r !== null,
    "status is accepted/rejected": (r) =>
      r !== null && (r.status === "accepted" || r.status === "rejected"),
  });

  if (txResult) {
    if (txResult.status === "accepted") accepted.add(1);
    else rejected.add(1);

    console.log(
      `[VU ${__VU}] ${txResult.status.toUpperCase()}${
        txResult.message ? " — " + txResult.message : ""
      }`,
    );
  } else {
    console.error(
      `[VU ${__VU}] No response received (timeout or connection error)`,
    );
  }
}

// ── Summary ───────────────────────────────────────────────────────────────────

export function handleSummary(data) {
  const acc = data.metrics["transactions_accepted"]?.values?.count ?? 0;
  const rej = data.metrics["transactions_rejected"]?.values?.count ?? 0;

  console.log("\n╔══════════════════════════════════════════╗");
  console.log("║        Bank Race Condition Result        ║");
  console.log("╠══════════════════════════════════════════╣");
  console.log(`║  Accepted : ${String(acc).padEnd(29)}║`);
  console.log(`║  Rejected : ${String(rej).padEnd(29)}║`);
  console.log(
    `║  Lock OK  : ${String(acc <= 1 ? "✓ YES — global lock held" : "✗ NO — RACE CONDITION!").padEnd(29)}║`,
  );
  console.log("╚══════════════════════════════════════════╝\n");

  return {};
}

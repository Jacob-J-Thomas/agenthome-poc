import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const sleep = (milliseconds) =>
  new Promise((resolve) => setTimeout(resolve, milliseconds));

function parseBoundedInteger(value, name, minimum, maximum) {
  if (!/^(0|[1-9][0-9]*)$/.test(value ?? "")) {
    throw new Error(`${name} must be a canonical nonnegative integer.`);
  }

  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    throw new Error(`${name} must be between ${minimum} and ${maximum}.`);
  }

  return parsed;
}

function countMarkers(root, suffix) {
  return fs
    .readdirSync(root, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(suffix)).length;
}

async function waitForMarkerCount(root, suffix, expectedCount) {
  const deadline = Date.now() + 20_000;
  while (countMarkers(root, suffix) < expectedCount) {
    if (Date.now() >= deadline) {
      return false;
    }

    await sleep(10);
  }

  return true;
}

async function waitForFile(filePath, deadline) {
  while (!fs.existsSync(filePath)) {
    if (Date.now() >= deadline) {
      return false;
    }

    await sleep(10);
  }

  return true;
}

async function runBasic(args) {
  const [
    name,
    delayText,
    exitCodeText,
    orderPath = "-",
    synchronizationRoot = "-",
    expectedConcurrentText = "0",
  ] = args;
  if (!name) {
    return 2;
  }

  const delayMilliseconds = parseBoundedInteger(
    delayText,
    "delayMilliseconds",
    0,
    60_000,
  );
  const exitCode = parseBoundedInteger(exitCodeText, "exitCode", 0, 255);
  const expectedConcurrent = parseBoundedInteger(
    expectedConcurrentText,
    "expectedConcurrent",
    0,
    64,
  );
  if (orderPath !== "-") {
    fs.appendFileSync(orderPath, `${name}${os.EOL}`, { encoding: "utf8" });
  }

  if (synchronizationRoot !== "-" && expectedConcurrent > 0) {
    fs.mkdirSync(synchronizationRoot, { recursive: true });
    fs.writeFileSync(path.join(synchronizationRoot, `${name}.ready`), "ready", {
      encoding: "utf8",
    });
    if (
      !(await waitForMarkerCount(
        synchronizationRoot,
        ".ready",
        expectedConcurrent,
      ))
    ) {
      return 41;
    }
  }

  await sleep(delayMilliseconds);
  console.log(`probe=${name}`);
  console.log(`environment=${process.env.VERIFY_PARALLEL_PROBE ?? ""}`);
  console.log(`physical_temp=${path.resolve(os.tmpdir())}`);
  return exitCode;
}

async function runWeighted(args) {
  const [
    name,
    activeRoot,
    expectedConcurrentText,
    maximumExpectedConcurrentText,
  ] = args;
  if (!name || !activeRoot) {
    return 2;
  }

  const expectedConcurrent = parseBoundedInteger(
    expectedConcurrentText,
    "expectedConcurrent",
    1,
    64,
  );
  const maximumExpectedConcurrent = parseBoundedInteger(
    maximumExpectedConcurrentText,
    "maximumExpectedConcurrent",
    1,
    64,
  );
  fs.mkdirSync(activeRoot, { recursive: true });
  const activePath = path.join(activeRoot, `${name}.active`);
  try {
    fs.writeFileSync(activePath, "active", { encoding: "utf8" });
    if (
      !(await waitForMarkerCount(activeRoot, ".active", expectedConcurrent))
    ) {
      return 42;
    }

    const deadline = Date.now() + 250;
    while (Date.now() < deadline) {
      const activeCount = countMarkers(activeRoot, ".active");
      if (activeCount > maximumExpectedConcurrent) {
        console.log(`overcommitted=${activeCount}`);
        return 43;
      }

      await sleep(10);
    }

    console.log(`weighted_probe=${name}`);
    return 0;
  } finally {
    fs.rmSync(activePath, { force: true });
  }
}

async function runTimed(args) {
  const [role, synchronizationRoot] = args;
  if (!role || !synchronizationRoot) {
    return 2;
  }

  const timestamp = () => process.hrtime.bigint().toString();
  console.log(`start=${timestamp()}`);
  fs.writeFileSync(
    path.join(synchronizationRoot, `${role}.started`),
    timestamp(),
    { encoding: "utf8" },
  );
  if (role === "build") {
    const deadline = Date.now() + 20_000;
    for (const requiredRole of ["ordinary", "nested-first", "nested-second"]) {
      if (
        !(await waitForFile(
          path.join(synchronizationRoot, `${requiredRole}.started`),
          deadline,
        ))
      ) {
        throw new Error(
          `Timed out waiting for required preflight overlap marker: ${requiredRole}`,
        );
      }
    }

    await sleep(200);
  } else if (role === "frontend") {
    await sleep(150);
  } else if (role === "coverage" || role === "prepare") {
    const peerRole = role === "coverage" ? "prepare" : "coverage";
    if (
      !(await waitForFile(
        path.join(synchronizationRoot, `${peerRole}.started`),
        Date.now() + 20_000,
      ))
    ) {
      throw new Error(
        `Timed out waiting for the sibling post-build process-heavy phase: ${peerRole}`,
      );
    }

    await sleep(200);
  } else if (role.startsWith("format-")) {
    await sleep(1_000);
  } else if (role === "ordinary") {
    if (
      !(await waitForFile(
        path.join(synchronizationRoot, "coverage.started"),
        Date.now() + 20_000,
      ))
    ) {
      throw new Error(
        "Timed out waiting for the build-dependent coverage phase to overlap ordinary work.",
      );
    }

    await sleep(200);
  } else if (role.startsWith("nested-")) {
    await sleep(200);
  }

  console.log(`end=${timestamp()}`);
  return 0;
}

async function runNpm(args) {
  const [operation] = args;
  const orderPath = process.env.EMBODYSENSE_FAKE_NPM_ORDER_PATH;
  const pidPath = process.env.EMBODYSENSE_FAKE_NPM_PID_PATH;
  if (!operation || !orderPath || !pidPath) {
    return 2;
  }

  fs.appendFileSync(orderPath, `${operation}${os.EOL}`, { encoding: "utf8" });
  fs.writeFileSync(pidPath, process.pid.toString(), { encoding: "utf8" });
  console.log(`fake-${operation}-output`);
  if (operation === "ci") {
    const delayMilliseconds = parseBoundedInteger(
      process.env.EMBODYSENSE_FAKE_NPM_INSTALL_DELAY_MILLISECONDS,
      "installDelayMilliseconds",
      0,
      60_000,
    );
    const exitCode = parseBoundedInteger(
      process.env.EMBODYSENSE_FAKE_NPM_INSTALL_EXIT_CODE,
      "installExitCode",
      0,
      255,
    );
    await sleep(delayMilliseconds);
    return exitCode;
  }

  return operation === "test"
    ? parseBoundedInteger(
        process.env.EMBODYSENSE_FAKE_NPM_TEST_EXIT_CODE,
        "testExitCode",
        0,
        255,
      )
    : 97;
}

try {
  const [mode, ...modeArguments] = process.argv.slice(2);
  process.exitCode =
    mode === "basic"
      ? await runBasic(modeArguments)
      : mode === "weighted"
        ? await runWeighted(modeArguments)
        : mode === "timed"
          ? await runTimed(modeArguments)
          : mode === "npm"
            ? await runNpm(modeArguments)
            : 2;
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 99;
}

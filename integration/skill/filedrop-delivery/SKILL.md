---
name: filedrop-delivery
description: Route explicitly requested image, screenshot, document or file deliveries through native delivery first and the local FileDrop MCP fallback policy. Use only for delivery; do not capture screenshots, scan folders or reroute unrelated tools.
---

For an explicitly requested delivery, call get_delivery_policy with stable symbolic channel/capability and the active task's opaque ID. Use separate capabilities for image and document delivery when their native mechanisms differ. Do not put URLs, secrets, filenames or raw error text in those identifiers.

- native / mayTryNative=true: attempt the native delivery once. Report the observed result with record_delivery_outcome.
- A clear technical failure can be reported as technical_failure. It activates FileDrop for that channel/capability for 24 hours and pins that active task. Do not keep trying the failed native path.
- If the clear failure was already observed before consulting policy, do not repeat native just to demonstrate it: consult the scope and report that observed result when a native attempt is reserved, or use an already-active fallback.
- denied, cancelled and pending_confirmation are stopping conditions, never technical failures or permission to change delivery channel. Stop/wait and explain the blocker.
- uncertain / verify_result: check whether the existing delivery arrived before any new attempt. Do not auto retry, switch channels, or change task IDs. Report delivered only after confirmation; verified_not_delivered only after confirmed non-delivery.
- filedrop / mayPublish=true: call publish_file for one requested existing absolute file path, with the same scope/task identifiers. Only configured roots are allowed. Never change roots, endpoint, permissions or credentials to escape a denial.
- The publisher POSTs once, refuses redirects, verifies the returned URL by GET, and returns JSON. After an uploaded_unverified/uncertain result, do not POST again automatically. Keep any returned URL only in the authorized conversation/memory so it can be checked.
- Communicate the actual result and link to the user. The URL is a bearer credential: anyone with it can read the file; loss prevents recovery. Do not save/log the URL, tool response, Location, credential or file content. Deliberate response to the authorized consumer is allowed.
- At the actual end of the task call end_delivery_task. Do not end/recreate tasks to bypass blocked outcomes or a pinned fallback.
- After cooldown, a genuinely new task may receive one new native probe. If it technically fails, report it to renew fallback for 24 hours. Do not probe during the pinned active task.
- Native tools and this MCP are independent: the MCP cannot observe or intercept native results. This instruction and explicit outcome reports coordinate the routing.
- The MCP only delivers existing files; capture/generation stays with existing authorized tools. No folder scanning or unsolicited uploads. Preserve the user's authorization and all higher-priority permission constraints.

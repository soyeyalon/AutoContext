---
name: "dotnet-unity (v1.0.0)"
description: "Apply when writing or reviewing Unity code (MonoBehaviour lifecycle, component caching, physics, coroutines, ScriptableObjects)."
applyTo: "**/*.cs"
---

# Unity Instructions

## MCP Tool Validation

After editing or generating any C# source file, call the
`analyze_csharp_code_style` and `analyze_csharp_project_structure`
MCP tools on the changed source. Pass the file contents as `content`
and the file's absolute path as `filePath`. For test files, also call
`analyze_csharp_testing_style` with the same `content` and `filePath`,
plus the test project's directory as `projectDirectory`. Treat any
reported violation as blocking — fix it before reporting the work as
done.

## Rules

### MonoBehaviour Lifecycle

- [INST0001] **Do** use `Awake` for self-initialization and caching component references — `Awake` runs before any `Start` call, making it the right place to set up state that other components may read in their own `Start`.
- [INST0002] **Do** use `Start` for initialization that depends on other GameObjects or components — `Start` runs after all `Awake` calls have completed, so all objects are guaranteed to be initialized.
- [INST0003] **Do** subscribe to events in `OnEnable` and unsubscribe in `OnDisable` — this ensures handlers are not invoked when the object is inactive and prevents memory leaks when the object is destroyed while still subscribed.
- [INST0004] **Do** put physics updates and `Rigidbody` manipulation in `FixedUpdate`, not `Update` — `FixedUpdate` runs at the fixed physics timestep; running physics logic in `Update` produces frame-rate-dependent behavior.
- [INST0005] **Don't** leave `Update`, `FixedUpdate`, or `LateUpdate` methods on a `MonoBehaviour` if they contain no logic — Unity invokes every overridden Update-family method via a native callback with non-trivial overhead even when the method body is empty.

### Component References & Caching

- [INST0006] **Do** call `GetComponent<T>()` in `Awake` or `Start` and store the result in a field — calling `GetComponent<T>()` every frame performs a type search over the component list and is one of the most common Unity performance pitfalls.
- [INST0007] **Do** prefer `[SerializeField]` on private fields to assign component references in the Inspector — this avoids runtime `GetComponent` calls entirely and makes dependencies explicit.
- [INST0008] **Don't** use `GameObject.Find()`, `FindObjectOfType<T>()`, or `FindObjectsOfType<T>()` at runtime in frequently-called code — these methods do a full scene scan and are O(n) in the number of scene objects; reserve them for one-time initialization in `Awake` if truly necessary.
- [INST0009] **Don't** use string-based access such as `animator.Play("StateName")` without caching — use `Animator.StringToHash` for parameter and state identifiers to avoid per-call string allocations and silent mismatches.
- [INST0010] **Don't** make fields `public` solely to appear in the Inspector — use `[SerializeField]` on `private` fields instead; public fields expose implementation details to other scripts and bypass encapsulation.

### Physics

- [INST0011] **Do** use `Rigidbody.MovePosition` and `Rigidbody.MoveRotation` to move kinematic `Rigidbody` objects — direct `Transform` assignment bypasses the physics engine, breaking continuous collision detection and inter-object contact resolution.
- [INST0012] **Don't** modify `Transform.position` directly on a `GameObject` that has a non-kinematic `Rigidbody` — this teleports the body and causes tunnelling through colliders; use forces, velocity, or `Rigidbody.MovePosition` for physically-simulated objects.

### Coroutines & Async

- [INST0013] **Do** cache `WaitForSeconds`, `WaitForFixedUpdate`, and other `YieldInstruction` instances as static or instance fields — each `new WaitForSeconds(1f)` allocates a managed object; reusing the instance eliminates per-frame garbage.
- [INST0014] **Do** stop coroutines in `OnDisable` with `StopCoroutine` or `StopAllCoroutines` — coroutines continue running after a `MonoBehaviour` is disabled if not explicitly stopped, executing callbacks on an inactive object.
- [INST0015] **Do** prefer `async`/`await` with `Awaitable` (Unity 6+) or UniTask over coroutines for complex multi-step asynchronous flows — they support `CancellationToken`, have richer error handling, and are easier to compose; tie a `CancellationTokenSource` to the object's `OnDestroy` to prevent post-destroy callbacks; avoid raw `Task.Delay` or `Task.Run` in Unity as they bypass the main thread and allocate on the managed heap.
- [INST0016] **Don't** use `Invoke("MethodName", delay)` or `InvokeRepeating` — they rely on string-based reflection that fails silently when the method is renamed; use a coroutine, `async` delay, or a timer field in `Update` instead.

### ScriptableObjects

- [INST0017] **Do** use `ScriptableObject` assets for shared configuration, constants, and game parameters — they are referenced directly from the asset database and avoid duplicating data across prefab instances.
- [INST0018] **Do** treat `ScriptableObject` fields as read-only at runtime in shipped builds — changes made during Play Mode in the Editor are persistent (they modify the asset on disk), but the same changes are discarded in builds; mutable runtime state belongs in plain C# classes or `MonoBehaviour` instances.
- [INST0019] **Don't** use `Resources.Load<ScriptableObject>()` to load configuration assets — prefer direct Inspector references or Addressables; `Resources` folders bypass the asset pipeline, inflate build size with unused assets, and are deprecated for new projects.

### Performance

- [INST0020] **Do** use `CompareTag("TagName")` instead of `gameObject.tag == "TagName"` — the equality check creates a temporary `string` allocation on every call; `CompareTag` avoids the allocation.
- [INST0021] **Do** use object pooling (e.g., `ObjectPool<T>` from `UnityEngine.Pool`) for `GameObject`s that are frequently instantiated and destroyed — `Instantiate` and `Destroy` trigger GC allocations, asset loading, and frame-time hitches.
- [INST0022] **Don't** call LINQ methods or perform string concatenation in `Update`, `FixedUpdate`, or `LateUpdate` — these allocate managed heap memory every frame, causing frequent GC collections and frame stutters; use pre-allocated arrays or index-based loops instead.
- [INST0023] **Don't** call `Debug.Log` in per-frame code in release builds — each call allocates a string and has non-trivial cost even without the Editor connected; guard with `#if UNITY_EDITOR` or route through a custom logger that strips calls in release builds.

### Scene Management

- [INST0024] **Do** load scenes additively with `LoadSceneMode.Additive` when composing levels from multiple scene files — additive loading keeps the current scene resident, preventing the destruction and re-creation of persistent objects and manager singletons.
- [INST0025] **Don't** use `DontDestroyOnLoad` liberally — it creates implicit global state that is difficult to test and reason about; prefer explicit lifecycle management via ScriptableObject event channels or a dependency-injection framework such as VContainer or Zenject.

### Code Organization & Testability

- [INST0026] **Do** separate game logic from `MonoBehaviour` — extract domain rules into plain C# classes that can be unit-tested without running a Unity scene; keep `MonoBehaviour`s as thin adapters that wire input, callbacks, and display to the logic layer.
- [INST0027] **Do** use the Unity Test Framework (NUnit-based) with `EditMode` and `PlayMode` test assemblies — `EditMode` tests run without entering Play Mode and execute much faster; reserve `PlayMode` tests for behavior that requires a running physics or render loop.

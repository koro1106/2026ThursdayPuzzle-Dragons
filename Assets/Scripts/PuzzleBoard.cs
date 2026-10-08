using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// パズドラ風ドロップパズル（盤面部分）
/// 使い方：空のGameObjectにこのスクリプトを付けて再生するだけ。
/// 画像素材なしでも動作（Inspector の Orb Sprites に画像を入れるとそれを使用）。
///
/// ・ドロップを掴んでドラッグすると、通過したドロップと入れ替わる（斜め移動も可）
/// ・最初に動かしてから制限時間が経つか、指を離すと確定
/// ・縦横3つ以上揃うと消える → 落下 → 補充 → 連鎖（落ちコン）
/// </summary>
public class PuzzleBoard : MonoBehaviour
{
    [Header("盤面サイズ")]
    public int columns = 6;
    public int rows = 5;
    public float cellSize = 1f;

    [Header("画面レイアウト")]
    [Tooltip("ON：盤面を画面の横幅いっぱいにして下に寄せる（パズドラ風）")]
    public bool boardAtBottom = true;
    [Tooltip("盤面の左右の余白（マス数単位）")]
    public float sideMargin = 0f;
    [Tooltip("盤面の下の余白（マス数単位）")]
    public float bottomMargin = 0.3f;

    [Header("ドロップの色（配列の長さ = ドロップの種類数）")]
    public Color[] orbColors =
    {
        new Color(0.95f, 0.30f, 0.30f), // 火
        new Color(0.30f, 0.55f, 1.00f), // 水
        new Color(0.35f, 0.85f, 0.40f), // 木
        new Color(1.00f, 0.85f, 0.30f), // 光
        new Color(0.70f, 0.40f, 0.95f), // 闇
        new Color(1.00f, 0.50f, 0.75f), // 回復
    };

    [Header("ドロップの画像（設定すると色より優先。配列の長さ = ドロップの種類数）")]
    [Tooltip("火・水・木・光・闇・回復などの画像を順番に入れる。空欄の要素は色付きの丸で表示")]
    public Sprite[] orbSprites;
    [Tooltip("画像にも色を重ねたい場合はON（通常はOFF）")]
    public bool tintSprites = false;

    [Header("操作")]
    [Tooltip("最初にドロップを動かしてからの制限時間（秒）")]
    public float moveTimeLimit = 4f;
    [Tooltip("マスの中心からこの距離（セルサイズ比）以内に入ったら入れ替え。小さいほど斜め移動しやすい")]
    [Range(0.2f, 0.5f)] public float swapRadius = 0.42f;

    [Header("演出")]
    public float swapSpeed = 25f;
    public float fallSpeed = 14f;
    public float clearDuration = 0.3f;
    [Range(0.5f, 1f)] public float orbScale = 0.95f;

    enum State { Idle, Dragging, Resolving }

    class Orb
    {
        public int type;
        public Transform tr;
        public SpriteRenderer sr;
        public Vector3 target;
        public float speed;
        public float baseScale;
    }

    /// <summary>ドロップの種類数（画像が設定されていれば画像の数）</summary>
    int TypeCount => (orbSprites != null && orbSprites.Length > 0) ? orbSprites.Length : orbColors.Length;

    Orb[,] grid;
    State state = State.Idle;
    Camera cam;
    Sprite orbSprite;
    Sprite tileSprite;

    // ドラッグ中の情報
    Orb heldOrb;
    Vector2Int heldCell;
    bool moveStarted;
    float moveTimer;

    // コンボ表示
    int comboCount;
    float comboShowTimer;
    GUIStyle comboStyle;

    // ------------------------------------------------------------
    // 初期化
    // ------------------------------------------------------------
    void Start()
    {
        cam = Camera.main;
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        SetupCamera();

        orbSprite = CreateOrbSprite(128);
        tileSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);

        CreateBackground();

        grid = new Orb[columns, rows];
        for (int x = 0; x < columns; x++)
            for (int y = 0; y < rows; y++)
                grid[x, y] = CreateOrb(RandomTypeWithoutMatch(x, y), CellToWorld(x, y));
    }

    void SetupCamera()
    {
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.08f, 0.08f, 0.12f);
        UpdateCameraSize();
    }

    void UpdateCameraSize()
    {
        float boardHalfW = columns * cellSize * 0.5f + sideMargin * cellSize;
        float boardHalfH = rows * cellSize * 0.5f;

        if (boardAtBottom)
        {
            // 横幅いっぱいに合わせる（横長画面のときは盤面が収まるように縦で合わせる）
            float size = Mathf.Max(boardHalfW / cam.aspect, boardHalfH + bottomMargin * cellSize);
            cam.orthographicSize = size;

            // 盤面の下端が画面下端 + 余白 に来るようにカメラを上へずらす
            float boardBottom = -boardHalfH - bottomMargin * cellSize;
            cam.transform.position = transform.position + new Vector3(0f, boardBottom + size, -10f);
        }
        else
        {
            float halfH = boardHalfH + cellSize;   // 上にUI用の余白
            cam.orthographicSize = Mathf.Max(halfH, (boardHalfW + cellSize * 0.3f) / cam.aspect);
            cam.transform.position = transform.position + new Vector3(0f, 0.5f * cellSize, -10f);
        }
    }

    /// <summary>盤面の上端の画面Y座標（GUI座標系：上が0）</summary>
    float BoardTopGuiY()
    {
        Vector3 top = transform.position + new Vector3(0f, rows * cellSize * 0.5f, 0f);
        return Screen.height - cam.WorldToScreenPoint(top).y;
    }

    void CreateBackground()
    {
        var bg = new GameObject("BoardBackground").transform;
        bg.SetParent(transform, false);
        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                var go = new GameObject($"Tile_{x}_{y}");
                go.transform.SetParent(bg, false);
                go.transform.position = CellToWorld(x, y);
                go.transform.localScale = Vector3.one * cellSize;
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = tileSprite;
                sr.color = ((x + y) % 2 == 0) ? new Color(0.24f, 0.19f, 0.16f) : new Color(0.18f, 0.14f, 0.12f);
                sr.sortingOrder = -10;
            }
        }
    }

    /// <summary>白い球体風の画像を生成（色はSpriteRendererで着色）</summary>
    Sprite CreateOrbSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        var px = new Color[size * size];
        float r = size * 0.5f;
        var center = new Vector2(r, r);
        var highlight = new Vector2(size * 0.36f, size * 0.66f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Vector2.Distance(p, center) / r;                 // 0=中心, 1=縁
                float alpha = Mathf.Clamp01((1f - d) * r * 0.5f);          // 縁のアンチエイリアス
                float shade = Mathf.Lerp(1f, 0.55f, d * d);                // 縁ほど暗く
                float h = Mathf.Clamp01(1f - Vector2.Distance(p, highlight) / (size * 0.22f));
                float v = Mathf.Clamp01(shade + h * h * 0.6f);             // ハイライト
                px[y * size + x] = new Color(v, v, v, alpha);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    Orb CreateOrb(int type, Vector3 pos)
    {
        var go = new GameObject("Orb");
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        var sr = go.AddComponent<SpriteRenderer>();

        Sprite custom = (orbSprites != null && type < orbSprites.Length) ? orbSprites[type] : null;
        Color fallbackColor = orbColors.Length > 0 ? orbColors[type % orbColors.Length] : Color.white;
        if (custom != null)
        {
            sr.sprite = custom;
            sr.color = tintSprites ? fallbackColor : Color.white;
        }
        else
        {
            sr.sprite = orbSprite;
            sr.color = fallbackColor;
        }

        // 画像の大きさ（解像度・Pixels Per Unit）に関係なく1マスに収まるよう調整
        Vector2 size = sr.sprite.bounds.size;
        float fit = Mathf.Max(size.x, size.y);
        float baseScale = cellSize * orbScale / (fit > 0f ? fit : 1f);
        go.transform.localScale = Vector3.one * baseScale;

        return new Orb { type = type, tr = go.transform, sr = sr, target = pos, speed = fallSpeed, baseScale = baseScale };
    }

    /// <summary>初期盤面で最初から揃っていないように種類を選ぶ</summary>
    int RandomTypeWithoutMatch(int x, int y)
    {
        for (int tries = 0; tries < 100; tries++)
        {
            int t = Random.Range(0, TypeCount);
            if (x >= 2 && grid[x - 1, y].type == t && grid[x - 2, y].type == t) continue;
            if (y >= 2 && grid[x, y - 1].type == t && grid[x, y - 2].type == t) continue;
            return t;
        }
        return Random.Range(0, TypeCount);
    }

    // ------------------------------------------------------------
    // 座標変換
    // ------------------------------------------------------------
    Vector3 CellToWorld(int x, int y)
    {
        return transform.position + new Vector3(
            (x - (columns - 1) * 0.5f) * cellSize,
            (y - (rows - 1) * 0.5f) * cellSize,
            0f);
    }

    Vector3 CellToWorld(Vector2Int c) => CellToWorld(c.x, c.y);

    Vector2Int WorldToCell(Vector3 world)
    {
        Vector3 local = world - transform.position;
        int x = Mathf.RoundToInt(local.x / cellSize + (columns - 1) * 0.5f);
        int y = Mathf.RoundToInt(local.y / cellSize + (rows - 1) * 0.5f);
        return new Vector2Int(Mathf.Clamp(x, 0, columns - 1), Mathf.Clamp(y, 0, rows - 1));
    }

    // ------------------------------------------------------------
    // 毎フレーム
    // ------------------------------------------------------------
    void Update()
    {
        UpdateCameraSize();
        HandleInput();
        AnimateOrbs();

        if (comboShowTimer > 0f && state != State.Resolving)
            comboShowTimer -= Time.deltaTime;
    }

    bool ReadPointer(out Vector2 pos, out bool down, out bool held)
    {
#if ENABLE_INPUT_SYSTEM
        var p = Pointer.current;
        if (p == null) { pos = default; down = held = false; return false; }
        pos = p.position.ReadValue();
        down = p.press.wasPressedThisFrame;
        held = p.press.isPressed;
        return true;
#else
        pos = Input.mousePosition;
        down = Input.GetMouseButtonDown(0);
        held = Input.GetMouseButton(0);
        return true;
#endif
    }

    void HandleInput()
    {
        if (state == State.Resolving) return;

        if (!ReadPointer(out Vector2 screenPos, out bool down, out bool held))
        {
            if (state == State.Dragging) Release();
            return;
        }

        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 10f));
        world.z = 0f;

        if (state == State.Idle)
        {
            if (down) TryPick(world);
            return;
        }

        // Dragging
        if (!held) { Release(); return; }

        DragTo(world);

        if (moveStarted)
        {
            moveTimer -= Time.deltaTime;
            if (moveTimer <= 0f) Release();
        }
    }

    void TryPick(Vector3 world)
    {
        Vector3 local = world - transform.position;
        if (Mathf.Abs(local.x) > columns * cellSize * 0.5f || Mathf.Abs(local.y) > rows * cellSize * 0.5f)
            return;

        Vector2Int c = WorldToCell(world);
        heldOrb = grid[c.x, c.y];
        if (heldOrb == null) return;

        heldCell = c;
        moveStarted = false;
        state = State.Dragging;

        heldOrb.sr.sortingOrder = 10;
        heldOrb.tr.localScale = Vector3.one * heldOrb.baseScale * 1.15f;
        var col = heldOrb.sr.color; col.a = 0.85f; heldOrb.sr.color = col;
    }

    void DragTo(Vector3 world)
    {
        // 盤面内に制限して指に追従
        Vector3 local = world - transform.position;
        float maxX = (columns - 1) * 0.5f * cellSize;
        float maxY = (rows - 1) * 0.5f * cellSize;
        local.x = Mathf.Clamp(local.x, -maxX, maxX);
        local.y = Mathf.Clamp(local.y, -maxY, maxY);
        Vector3 pos = transform.position + local;
        heldOrb.tr.position = pos;

        Vector2Int target = WorldToCell(pos);
        if (target == heldCell) return;

        // マスの中心付近に入ったときだけ入れ替える（角を通れば斜め移動になる）
        if ((CellToWorld(target) - pos).magnitude > cellSize * swapRadius) return;

        // 速く動かしてマスを飛ばした場合も1マスずつ入れ替える
        while (heldCell != target)
        {
            var step = new Vector2Int(System.Math.Sign(target.x - heldCell.x), System.Math.Sign(target.y - heldCell.y));
            SwapHeldInto(heldCell + step);
        }
    }

    void SwapHeldInto(Vector2Int next)
    {
        Orb other = grid[next.x, next.y];
        grid[heldCell.x, heldCell.y] = other;
        if (other != null)
        {
            other.target = CellToWorld(heldCell);
            other.speed = swapSpeed;
        }
        grid[next.x, next.y] = heldOrb;
        heldCell = next;

        if (!moveStarted)
        {
            moveStarted = true;
            moveTimer = moveTimeLimit;
        }
    }

    void Release()
    {
        heldOrb.sr.sortingOrder = 0;
        heldOrb.tr.localScale = Vector3.one * heldOrb.baseScale;
        var col = heldOrb.sr.color; col.a = 1f; heldOrb.sr.color = col;
        heldOrb.target = CellToWorld(heldCell);
        heldOrb.speed = swapSpeed;
        heldOrb = null;

        if (moveStarted) StartCoroutine(Resolve());
        else state = State.Idle;
    }

    void AnimateOrbs()
    {
        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                Orb o = grid[x, y];
                if (o == null || o == heldOrb) continue;
                o.tr.position = Vector3.MoveTowards(o.tr.position, o.target, o.speed * Time.deltaTime);
            }
        }
    }

    bool AllSettled()
    {
        foreach (Orb o in grid)
            if (o != null && o.tr.position != o.target) return false;
        return true;
    }

    // ------------------------------------------------------------
    // 消去・落下・連鎖
    // ------------------------------------------------------------
    IEnumerator Resolve()
    {
        state = State.Resolving;
        comboCount = 0;

        yield return new WaitUntil(AllSettled);

        while (true)
        {
            List<List<Vector2Int>> groups = FindMatchGroups();
            if (groups.Count == 0) break;

            foreach (var g in groups)
            {
                comboCount++;
                comboShowTimer = 1.5f;
                yield return StartCoroutine(ClearGroup(g));
            }

            ApplyGravityAndRefill();
            yield return new WaitUntil(AllSettled);
        }

        if (comboCount > 0) Debug.Log($"{comboCount} コンボ！");
        state = State.Idle;
    }

    /// <summary>
    /// 縦横3つ以上並んだドロップを探し、つながっている同色をまとめて1コンボとして返す
    /// </summary>
    List<List<Vector2Int>> FindMatchGroups()
    {
        var matched = new bool[columns, rows];

        // 横方向
        for (int y = 0; y < rows; y++)
        {
            int x = 0;
            while (x < columns)
            {
                if (grid[x, y] == null) { x++; continue; }
                int t = grid[x, y].type;
                int end = x + 1;
                while (end < columns && grid[end, y] != null && grid[end, y].type == t) end++;
                if (end - x >= 3)
                    for (int i = x; i < end; i++) matched[i, y] = true;
                x = end;
            }
        }

        // 縦方向
        for (int x = 0; x < columns; x++)
        {
            int y = 0;
            while (y < rows)
            {
                if (grid[x, y] == null) { y++; continue; }
                int t = grid[x, y].type;
                int end = y + 1;
                while (end < rows && grid[x, end] != null && grid[x, end].type == t) end++;
                if (end - y >= 3)
                    for (int i = y; i < end; i++) matched[x, i] = true;
                y = end;
            }
        }

        // 隣接している同色の消去対象を1グループにまとめる
        var groups = new List<List<Vector2Int>>();
        var visited = new bool[columns, rows];
        var dirs = new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                if (!matched[x, y] || visited[x, y]) continue;

                int t = grid[x, y].type;
                var group = new List<Vector2Int>();
                var queue = new Queue<Vector2Int>();
                queue.Enqueue(new Vector2Int(x, y));
                visited[x, y] = true;

                while (queue.Count > 0)
                {
                    Vector2Int c = queue.Dequeue();
                    group.Add(c);
                    foreach (var d in dirs)
                    {
                        Vector2Int n = c + d;
                        if (n.x < 0 || n.x >= columns || n.y < 0 || n.y >= rows) continue;
                        if (visited[n.x, n.y] || !matched[n.x, n.y]) continue;
                        if (grid[n.x, n.y].type != t) continue;
                        visited[n.x, n.y] = true;
                        queue.Enqueue(n);
                    }
                }
                groups.Add(group);
            }
        }
        return groups;
    }

    IEnumerator ClearGroup(List<Vector2Int> cells)
    {
        var orbs = new List<Orb>();
        foreach (var c in cells)
        {
            orbs.Add(grid[c.x, c.y]);
            grid[c.x, c.y] = null;
        }

        float t = 0f;
        while (t < clearDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / clearDuration);
            foreach (var o in orbs)
            {
                o.tr.localScale = Vector3.one * o.baseScale * (1f + k * 0.3f);
                var col = o.sr.color; col.a = 1f - k; o.sr.color = col;
            }
            yield return null;
        }

        foreach (var o in orbs) Destroy(o.tr.gameObject);
    }

    void ApplyGravityAndRefill()
    {
        for (int x = 0; x < columns; x++)
        {
            // 残っているドロップを下に詰める
            int write = 0;
            for (int y = 0; y < rows; y++)
            {
                Orb o = grid[x, y];
                if (o == null) continue;
                if (y != write)
                {
                    grid[x, write] = o;
                    grid[x, y] = null;
                    o.target = CellToWorld(x, write);
                    o.speed = fallSpeed;
                }
                write++;
            }

            // 空いた分を上から補充
            int spawned = 0;
            for (int y = write; y < rows; y++)
            {
                Vector3 spawnPos = CellToWorld(x, rows + spawned);
                Orb o = CreateOrb(Random.Range(0, TypeCount), spawnPos);
                o.target = CellToWorld(x, y);
                o.speed = fallSpeed;
                grid[x, y] = o;
                spawned++;
            }
        }
    }

    // ------------------------------------------------------------
    // 簡易UI（操作時間バー・コンボ数）
    // ------------------------------------------------------------
    void OnGUI()
    {
        if (comboStyle == null)
        {
            comboStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            comboStyle.normal.textColor = Color.white;
        }

        float boardTop = BoardTopGuiY();

        if (state == State.Dragging && moveStarted)
        {
            float ratio = Mathf.Clamp01(moveTimer / moveTimeLimit);
            float w = Screen.width * 0.9f;
            float h = Mathf.Max(8f, Screen.height * 0.012f);
            float x = (Screen.width - w) * 0.5f;
            float y = boardTop - h - Screen.height * 0.01f;   // 盤面のすぐ上

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = Color.Lerp(new Color(1f, 0.3f, 0.3f), new Color(0.3f, 1f, 0.5f), ratio);
            GUI.DrawTexture(new Rect(x, y, w * ratio, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        if (comboShowTimer > 0f && comboCount > 0)
        {
            comboStyle.fontSize = Mathf.RoundToInt(Mathf.Min(Screen.height * 0.045f, Screen.width * 0.09f));
            float lh = comboStyle.fontSize * 1.5f;
            GUI.Label(new Rect(0, boardTop - lh - Screen.height * 0.01f, Screen.width, lh),
                      $"{comboCount} COMBO", comboStyle);
        }
    }
}

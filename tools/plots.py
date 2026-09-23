
from __future__ import annotations

import math
import re
import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib.colors import BoundaryNorm, ListedColormap
from matplotlib.patches import Patch

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
_results_arg = sys.argv[1] if len(sys.argv) > 1 else "results"
RESULTS_DIR = (REPO_ROOT / _results_arg).resolve()
FIGURES_DIR = RESULTS_DIR / "figures"

plt.rcParams.update(
    {
        "font.size": 11,
        "axes.labelsize": 11,
        "axes.titlesize": 11,
        "legend.fontsize": 10,
        "xtick.labelsize": 10,
        "ytick.labelsize": 10,
        "figure.dpi": 100,
        "savefig.dpi": 300,
        "axes.grid": False,
    }
)

STRATEGY_COLORS = {
    "Boustrophedon": "#0072B2",
    "SpiralInward": "#E69F00",
    "RandomWalk": "#009E73",
}
STRATEGY_MARKERS = {
    "Boustrophedon": "o",
    "SpiralInward": "s",
    "RandomWalk": "^",
}
STRATEGY_LABELS = {
    "Boustrophedon": "Boustrophedon",
    "SpiralInward": "Spiralna strategija (SpiralInward)",
    "RandomWalk": "Nasumična putanja (RandomWalk)",
}
STRATEGY_ORDER = ["Boustrophedon", "SpiralInward", "RandomWalk"]

SIGMA_COLORS = ["#D55E00", "#56B4E9", "#CC79A7", "#F0E442"]
SINGLE_SERIES_COLOR = "#0072B2"

STRATEGY_SLUGS = {
    "Boustrophedon": "boustrophedon",
    "SpiralInward": "spirala",
    "RandomWalk": "nasumicna",
}

START_MARKER_COLOR = "#F0E442"

MAP_POLYGONS = ["P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8"]
MAP_SPACING = 0.25

RASTER_STATES = [
    (-2, "#D55E00", "Izvan poligona, pokriveno"),
    (-1, "#D3D3D3", "Izvan poligona"),
    (0, "#FFFFFF", "Unutar poligona, nepokošeno"),
    (1, "#2CA02C", "Pokošeno jednom"),
    (2, "#145214", "Pokošeno 2+ puta"),
]
RASTER_LEGEND_ORDER = [-1, -2, 0, 1, 2]

GEOFENCE_HZ = 2.0
TYPICAL_VERTEX_RANGE = (4, 50)

DEG_TO_M = 111320.0

_POLYGON_NUM_RE = re.compile(r"(\d+)")


def _polygon_sort_key(polygon_id: str):
    match = _POLYGON_NUM_RE.search(str(polygon_id))
    return (int(match.group(1)) if match else 0, str(polygon_id))


def load_csv(filename: str) -> pd.DataFrame | None:
    path = RESULTS_DIR / filename
    if not path.exists():
        print(f"[preskočeno] Nedostaje datoteka: {path.relative_to(REPO_ROOT)}")
        return None
    try:
        df = pd.read_csv(path)
    except Exception as exc:
        print(f"[preskočeno] Greška pri čitanju {path.relative_to(REPO_ROOT)}: {exc}")
        return None
    if df.empty:
        print(f"[preskočeno] Datoteka je prazna: {path.relative_to(REPO_ROOT)}")
        return None
    return df


def pick_swath(df: pd.DataFrame, column: str = "swath_m") -> float | None:
    if column not in df.columns or df[column].dropna().empty:
        return None
    values = sorted(df[column].dropna().unique())
    for candidate in values:
        if abs(candidate - 0.25) < 1e-9:
            return candidate
    return values[0]


def save_fig(fig: plt.Figure, filename: str) -> None:
    FIGURES_DIR.mkdir(parents=True, exist_ok=True)
    out_path = FIGURES_DIR / filename
    fig.savefig(out_path, dpi=300, bbox_inches="tight")
    plt.close(fig)
    print(f"[spremljeno] {out_path.relative_to(REPO_ROOT)}")


def safe_graph(name: str, func) -> None:
    try:
        func()
    except Exception as exc:
        print(f"[preskočeno] {name} — neočekivana greška: {exc}")


def fmt_hr(value: float, decimals: int = 1) -> str:
    return f"{value:.{decimals}f}".replace(".", ",")


def _spacing_tokens(spacing: float) -> list[str]:
    tokens: list[str] = []
    for candidate in (f"{spacing:g}", f"{spacing:.2f}", f"{spacing:.1f}", f"{spacing:.3f}"):
        if candidate not in tokens:
            tokens.append(candidate)
    return tokens


def find_series_file(subdir: str, prefix: str, polygon: str, strategy: str, spacing: float) -> Path | None:

    base = RESULTS_DIR / subdir
    if not base.is_dir():
        return None
    for token in _spacing_tokens(spacing):
        path = base / f"{prefix}_{polygon}_{strategy}_{token}.csv"
        if path.exists():
            return path
    for path in sorted(base.glob(f"{prefix}_{polygon}_{strategy}_*.csv")):
        token = path.stem.rsplit("_", 1)[-1]
        try:
            if abs(float(token) - spacing) < 1e-9:
                return path
        except ValueError:
            continue
    return None


def load_route(polygon: str, strategy: str, spacing: float = MAP_SPACING) -> pd.DataFrame | None:
    path = find_series_file("routes", "route", polygon, strategy, spacing)
    if path is None:
        print(f"[preskočeno] Nedostaje datoteka rute: results/routes/route_{polygon}_{strategy}_{spacing:g}.csv")
        return None
    try:
        df = pd.read_csv(path)
    except Exception as exc:
        print(f"[preskočeno] Greška pri čitanju {path.relative_to(REPO_ROOT)}: {exc}")
        return None
    if df.empty:
        print(f"[preskočeno] Datoteka rute je prazna: {path.relative_to(REPO_ROOT)}")
        return None
    if "idx" in df.columns:
        df = df.sort_values("idx")
    return df


_META_KV_RE = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=(\S+)")


def load_raster(polygon: str, strategy: str, spacing: float = MAP_SPACING):

    path = find_series_file("rasters", "raster", polygon, strategy, spacing)
    if path is None:
        print(f"[preskočeno] Nedostaje raster: results/rasters/raster_{polygon}_{strategy}_{spacing:g}.csv")
        return None
    meta: dict[str, str] = {}
    try:
        with path.open("r", encoding="utf-8") as handle:
            for line in handle:
                if not line.lstrip().startswith("#"):
                    break
                for key, value in _META_KV_RE.findall(line):
                    meta[key] = value
        grid = np.atleast_2d(np.loadtxt(path, delimiter=",", comments="#"))
    except Exception as exc:
        print(f"[preskočeno] Greška pri čitanju {path.relative_to(REPO_ROOT)}: {exc}")
        return None
    if grid.size == 0:
        print(f"[preskočeno] Raster je prazan: {path.relative_to(REPO_ROOT)}")
        return None
    return meta, grid.astype(int)


def _meta_float(meta: dict[str, str], key: str, default: float | None = None) -> float | None:
    try:
        return float(meta[key])
    except (KeyError, TypeError, ValueError):
        return default


def polygon_outline(polygons: pd.DataFrame, polygon_id: str) -> np.ndarray | None:

    sub = polygons[polygons["polygon_id"] == polygon_id]
    if sub.empty:
        return None
    if "vertex_idx" in sub.columns:
        sub = sub.sort_values("vertex_idx")
    xy = sub[["east_m", "north_m"]].to_numpy(dtype=float)
    if len(xy) < 2:
        return None
    if not np.allclose(xy[0], xy[-1]):
        xy = np.vstack([xy, xy[0]])
    return xy


def set_map_limits(ax, outline: np.ndarray, extra_xy: list[np.ndarray] | None = None) -> None:

    xs = [outline[:, 0]]
    ys = [outline[:, 1]]
    for extra in extra_xy or []:
        if extra is not None and len(extra):
            xs.append(extra[:, 0])
            ys.append(extra[:, 1])
    x_min = float(min(a.min() for a in xs))
    x_max = float(max(a.max() for a in xs))
    y_min = float(min(a.min() for a in ys))
    y_max = float(max(a.max() for a in ys))
    margin = max(0.05 * max(x_max - x_min, y_max - y_min), 0.5)
    ax.set_xlim(x_min - margin, x_max + margin)
    ax.set_ylim(y_min - margin, y_max + margin)


def project_latlon(lat, lon, lat0: float, lon0: float):

    lat = np.asarray(lat, dtype=float)
    lon = np.asarray(lon, dtype=float)
    north = (lat - lat0) * DEG_TO_M
    east = (lon - lon0) * DEG_TO_M * math.cos(math.radians(lat0))
    return east, north


def graph_g1() -> None:
    df = load_csv("coverage.csv")
    if df is None:
        print("[preskočeno] G1 (pokrivenost vs razmak redova) — nedostaje coverage.csv")
        return

    sub = df[df["polygon_id"] == "P1"].copy()
    if sub.empty:
        print("[preskočeno] G1 — nema redaka za poligon P1 u coverage.csv")
        return

    swath = pick_swath(sub)
    if swath is None:
        print("[preskočeno] G1 — nema podataka o swath_m za poligon P1")
        return
    sub = sub[sub["swath_m"] == swath]

    fig, ax = plt.subplots(figsize=(6, 4))
    plotted = False
    for strategy in STRATEGY_ORDER:
        s = sub[sub["strategy"] == strategy].sort_values("spacing_m")
        if s["spacing_m"].nunique() < 2:
            continue
        ax.plot(
            s["spacing_m"],
            s["coverage_pct"],
            marker=STRATEGY_MARKERS.get(strategy, "o"),
            color=STRATEGY_COLORS.get(strategy),
            label=STRATEGY_LABELS.get(strategy, strategy),
        )
        plotted = True

    if not plotted:
        plt.close(fig)
        print("[preskočeno] G1 — nijedna strategija nema dovoljno točaka razmaka za crtu")
        return

    ax.set_xlabel("Razmak redova [m]")
    ax.set_ylabel("Pokrivenost [%]")
    ax.grid(True, alpha=0.3)
    ax.legend()
    save_fig(fig, "g1_pokrivenost_razmak.png")


def graph_g2() -> None:
    df = load_csv("coverage.csv")
    if df is None:
        print("[preskočeno] G2 (preklapanje vs razmak redova) — nedostaje coverage.csv")
        return

    sub = df[df["polygon_id"] == "P1"].copy()
    if sub.empty:
        print("[preskočeno] G2 — nema redaka za poligon P1 u coverage.csv")
        return

    swath = pick_swath(sub)
    if swath is None:
        print("[preskočeno] G2 — nema podataka o swath_m za poligon P1")
        return
    sub = sub[sub["swath_m"] == swath]

    fig, ax = plt.subplots(figsize=(6, 4))
    plotted = False
    for strategy in STRATEGY_ORDER:
        s = sub[sub["strategy"] == strategy].sort_values("spacing_m")
        if s["spacing_m"].nunique() < 2:
            continue
        ax.plot(
            s["spacing_m"],
            s["overlap_pct"],
            marker=STRATEGY_MARKERS.get(strategy, "o"),
            color=STRATEGY_COLORS.get(strategy),
            label=STRATEGY_LABELS.get(strategy, strategy),
        )
        plotted = True

    if not plotted:
        plt.close(fig)
        print("[preskočeno] G2 — nijedna strategija nema dovoljno točaka razmaka za crtu")
        return

    ax.set_xlabel("Razmak redova [m]")
    ax.set_ylabel("Preklapanje putanja [%]")
    ax.grid(True, alpha=0.3)
    ax.legend()
    save_fig(fig, "g2_preklapanje_razmak.png")


def graph_g3() -> None:
    df = load_csv("coverage.csv")
    if df is None:
        print("[preskočeno] G3 (pokrivenost po poligonima) — nedostaje coverage.csv")
        return

    swath = pick_swath(df)
    if swath is None:
        print("[preskočeno] G3 — nema podataka o swath_m")
        return
    sub = df[df["swath_m"] == swath].copy()
    if sub.empty:
        print(f"[preskočeno] G3 — nema redaka za swath_m={swath}")
        return

    present_strategies = [s for s in STRATEGY_ORDER if s in sub["strategy"].unique()]
    if not present_strategies:
        print("[preskočeno] G3 — nema prepoznatih strategija u coverage.csv")
        return

    polygons = sorted(sub["polygon_id"].unique(), key=_polygon_sort_key)

    for required in ("P1", "P8"):
        if required not in polygons:
            print(f"[upozorenje] G3 — poligon {required} nije pronađen u coverage.csv (swath={swath})")

    values: dict[tuple[str, str], float] = {}
    for strategy in present_strategies:
        s_df = sub[sub["strategy"] == strategy]
        spacings = sorted(s_df["spacing_m"].dropna().unique())
        rep_spacing = spacings[0] if spacings else None
        if rep_spacing is not None:
            chosen = s_df[s_df["spacing_m"] == rep_spacing]
        else:
            chosen = s_df
        for _, row in chosen.iterrows():
            key = (row["polygon_id"], strategy)
            if key not in values:
                values[key] = row["coverage_pct"]

    n_strategies = len(present_strategies)
    width = 0.8 / n_strategies
    x_positions = list(range(len(polygons)))

    fig, ax = plt.subplots(figsize=(max(6, 1.1 * len(polygons)), 4.5))
    for i, strategy in enumerate(present_strategies):
        offset = (i - (n_strategies - 1) / 2) * width
        bar_x = [x + offset for x in x_positions]
        bar_y = [values.get((polygon, strategy), float("nan")) for polygon in polygons]
        ax.bar(
            bar_x,
            bar_y,
            width=width,
            color=STRATEGY_COLORS.get(strategy),
            label=STRATEGY_LABELS.get(strategy, strategy),
        )

    ax.set_xticks(x_positions)
    ax.set_xticklabels(polygons)
    ax.set_xlabel("Poligon")
    ax.set_ylabel("Pokrivenost [%]")
    ax.grid(True, axis="y", alpha=0.3)
    ax.legend()
    save_fig(fig, "g3_pokrivenost_po_poligonima.png")


def _timing_strategies(df: pd.DataFrame) -> list[str]:
    strategies = [s for s in STRATEGY_ORDER if s in df["strategy"].unique()]
    strategies += [s for s in df["strategy"].unique() if s not in strategies]
    return strategies


def graph_g4a() -> None:
    df = load_csv("timing.csv")
    if df is None:
        print("[preskočeno] G4a (vrijeme izvođenja vs broj točaka rute) — nedostaje timing.csv")
        return

    sub = df[(df["route_points"] > 0) & (df["mean_ms"] > 0)]
    if sub.empty:
        print("[preskočeno] G4a — nema redaka s pozitivnim route_points i mean_ms")
        return

    strategies = _timing_strategies(sub)
    if not strategies:
        print("[preskočeno] G4a — nema podataka o strategijama u timing.csv")
        return

    fig, ax = plt.subplots(figsize=(6.5, 4.5))
    ax.set_xscale("log")
    ax.set_yscale("log")

    for strategy in strategies:
        s = sub[sub["strategy"] == strategy]
        if s.empty:
            continue
        color = STRATEGY_COLORS.get(strategy, "#999999")
        ax.scatter(
            s["route_points"],
            s["mean_ms"],
            color=color,
            marker=STRATEGY_MARKERS.get(strategy, "o"),
            label=STRATEGY_LABELS.get(strategy, strategy),
            alpha=0.85,
            edgecolors="black",
            linewidths=0.3,
            zorder=3,
        )

        x = s["route_points"].to_numpy(dtype=float)
        y = s["mean_ms"].to_numpy(dtype=float)
        if len(x) < 2 or np.allclose(x, x[0]):
            continue
        slope, intercept = np.polyfit(np.log(x), np.log(y), 1)
        fit_x = np.array([x.min(), x.max()], dtype=float)
        fit_y = np.exp(intercept) * fit_x**slope
        ax.plot(fit_x, fit_y, color=color, linestyle="--", linewidth=1.0, alpha=0.55, zorder=2)
        ax.annotate(
            f"nagib ≈ {fmt_hr(slope, 2)}",
            xy=(fit_x[1], fit_y[1]),
            xytext=(-4, 8),
            textcoords="offset points",
            ha="right",
            fontsize=9,
            color=color,
        )

    ax.set_xlabel("Broj točaka rute")
    ax.set_ylabel("Vrijeme izvođenja [ms]")
    ax.grid(True, which="major", alpha=0.3)
    ax.grid(True, which="minor", alpha=0.15)
    ax.legend(loc="upper left")
    save_fig(fig, "g4a_vrijeme_broj_tocaka.png")


def graph_g4b() -> None:
    df = load_csv("timing.csv")
    if df is None:
        print("[preskočeno] G4b (vrijeme po točki rute) — nedostaje timing.csv")
        return

    sub = df[df["route_points"] > 0].copy()
    dropped = len(df) - len(sub)
    if dropped:
        print(f"[upozorenje] G4b — izostavljeno {dropped} redaka s route_points = 0")
    if sub.empty:
        print("[preskočeno] G4b — nema redaka s route_points > 0")
        return

    sub["ns_per_point"] = sub["mean_ms"] * 1e6 / sub["route_points"]

    strategies = [s for s in _timing_strategies(sub) if not sub[sub["strategy"] == s].empty]
    if not strategies:
        print("[preskočeno] G4b — nema podataka o strategijama u timing.csv")
        return

    groups = [sub[sub["strategy"] == s]["ns_per_point"].to_numpy(dtype=float) for s in strategies]
    positions = list(range(1, len(strategies) + 1))

    fig, ax = plt.subplots(figsize=(6.5, 4.5))
    box = ax.boxplot(
        groups,
        positions=positions,
        widths=0.5,
        showfliers=False,
        patch_artist=True,
        medianprops={"color": "black", "linewidth": 1.4},
        whiskerprops={"color": "#444444"},
        capprops={"color": "#444444"},
    )
    for patch, strategy in zip(box["boxes"], strategies):
        patch.set_facecolor(STRATEGY_COLORS.get(strategy, "#999999"))
        patch.set_alpha(0.35)
        patch.set_edgecolor(STRATEGY_COLORS.get(strategy, "#999999"))

    rng = np.random.default_rng(12345)
    for pos, strategy, values in zip(positions, strategies, groups):
        jitter = rng.uniform(-0.14, 0.14, size=len(values))
        ax.scatter(
            pos + jitter,
            values,
            color=STRATEGY_COLORS.get(strategy, "#999999"),
            marker=STRATEGY_MARKERS.get(strategy, "o"),
            s=22,
            alpha=0.75,
            edgecolors="black",
            linewidths=0.3,
            zorder=3,
        )
        median = float(np.median(values))
        print(
            f"[podatak] G4b — {strategy}: medijan {median:.1f} ns/točki "
            f"(n = {len(values)}, raspon {values.min():.1f}–{values.max():.1f})"
        )

    ax.set_xticks(positions)
    ax.set_xticklabels([STRATEGY_LABELS.get(s, s).replace(" (", "\n(") for s in strategies])
    ax.set_xlabel("Strategija")
    ax.set_ylabel("Vrijeme po točki rute [ns]")
    ax.set_ylim(bottom=0)
    ax.grid(True, axis="y", alpha=0.3)
    save_fig(fig, "g4b_vrijeme_po_tocki.png")


def graph_g5() -> None:
    df = load_csv("pip_noise.csv")
    if df is None:
        print("[preskočeno] G5 (pogrešna klasifikacija vs udaljenost od ruba) — nedostaje pip_noise.csv")
        return

    sigmas = sorted(df["sigma_m"].dropna().unique())
    if not sigmas:
        print("[preskočeno] G5 — nema podataka o sigma_m u pip_noise.csv")
        return

    fig, ax = plt.subplots(figsize=(6, 4))
    for i, sigma in enumerate(sigmas):
        s = df[df["sigma_m"] == sigma].sort_values("distance_from_edge_m")
        color = SIGMA_COLORS[i % len(SIGMA_COLORS)]
        ax.plot(
            s["distance_from_edge_m"],
            s["misclassification_pct"],
            marker="o",
            color=color,
            label=f"σ = {sigma:g} m",
        )

    ax.set_xlabel("Udaljenost od ruba [m]")
    ax.set_ylabel("Vjerojatnost pogrešne klasifikacije [%]")
    ax.grid(True, alpha=0.3)
    ax.legend()
    save_fig(fig, "g5_pogresna_klasifikacija_sum.png")


def graph_g6() -> None:
    df = load_csv("schedule.csv")
    if df is None:
        print("[preskočeno] G6 (histogram kašnjenja okidanja) — nedostaje schedule.csv")
        return

    if "trigger_delay_s" not in df.columns:
        print("[preskočeno] G6 — stupac trigger_delay_s nedostaje u schedule.csv")
        return

    values = df["trigger_delay_s"].dropna()
    if values.empty:
        print("[preskočeno] G6 — stupac trigger_delay_s nema valjanih vrijednosti")
        return

    bin_count = min(15, max(10, values.nunique()))
    fig, ax = plt.subplots(figsize=(6, 4))
    ax.hist(values, bins=bin_count, color=SINGLE_SERIES_COLOR, edgecolor="black", alpha=0.85)
    ax.set_xlabel("Kašnjenje okidanja [s]")
    ax.set_ylabel("Broj pokusa")
    ax.grid(True, axis="y", alpha=0.3)
    save_fig(fig, "g6_kasnjenje_okidanja.png")

GEOFENCE_PANELS = [
    ("GS2", "GS2 — izlazak preko granice"),
    ("GS4", "GS4 — tolerancija ovisna o GPS točnosti"),
    ("GS5", "GS5 — histereza (dva odvojena proboja)"),
]


def graph_g11() -> None:
    df = load_csv("geofence.csv")
    if df is None:
        print("[preskočeno] G11 (geofencing) — nedostaje geofence.csv")
        return

    needed = {"scenario_id", "t_s", "distance_past_m", "inside", "tolerance_m", "stop_count"}
    missing = needed - set(df.columns)
    if missing:
        print(f"[preskočeno] G11 — nedostaju stupci u geofence.csv: {sorted(missing)}")
        return

    panels = [(sid, title) for sid, title in GEOFENCE_PANELS if sid in set(df["scenario_id"])]
    if not panels:
        print("[preskočeno] G11 — nijedan od scenarija GS2/GS4/GS5 nije u geofence.csv")
        return

    fig, axes = plt.subplots(len(panels), 1, figsize=(7, 3.1 * len(panels)), sharex=False)
    if len(panels) == 1:
        axes = [axes]

    for ax, (scenario_id, title) in zip(axes, panels):
        s = df[df["scenario_id"] == scenario_id].sort_values("t_s")

        ax.plot(s["t_s"], s["distance_past_m"], color=SINGLE_SERIES_COLOR, lw=1.4,
                label="udaljenost izvan zone")
        ax.plot(s["t_s"], s["tolerance_m"], color="#D55E00", lw=1.2, ls="--",
                label="tolerancija proboja = max(0,5 m, GPS točnost)")

        stop_deltas = s["stop_count"].diff().fillna(s["stop_count"].iloc[0])
        breach_rows = s[stop_deltas > 0]
        for _, row in breach_rows.iterrows():
            ax.axvline(row["t_s"], color="#CC2222", lw=1.0, ls=":")
        if not breach_rows.empty:
            ax.scatter(breach_rows["t_s"], breach_rows["distance_past_m"], color="#CC2222",
                       zorder=5, marker="x", s=50, label="Stop poslan")

        ax.set_ylabel("Udaljenost [m]")
        ax.set_title(title, fontsize=10, loc="left")
        ax.grid(True, alpha=0.3)
        ax.legend(fontsize=8, loc="upper left")

    axes[-1].set_xlabel("Vrijeme [s]")
    fig.tight_layout()
    save_fig(fig, "g11_geofencing_proboj.png")


def graph_g9() -> None:
    df = load_csv("pip_complexity.csv")
    if df is None:
        print("[preskočeno] G9 (složenost Contains()) — nedostaje pip_complexity.csv")
        return

    s = df.sort_values("vertices")
    s = s[(s["vertices"] > 0) & (s["mean_ns_per_call"] > 0)]
    if len(s) < 2:
        print("[preskočeno] G9 — premalo valjanih mjernih točaka u pip_complexity.csv")
        return

    vertices = s["vertices"].to_numpy(dtype=float)
    nanoseconds = s["mean_ns_per_call"].to_numpy(dtype=float)

    fig, ax = plt.subplots(figsize=(6.2, 4.2))
    ax.set_xscale("log")
    ax.set_yscale("log")

    low, high = TYPICAL_VERTEX_RANGE
    ax.axvspan(low, high, alpha=0.12, color=SINGLE_SERIES_COLOR, zorder=0)

    ideal_x = np.array([vertices.min(), vertices.max()], dtype=float)
    ideal_y = nanoseconds[0] * ideal_x / vertices[0]
    ax.plot(
        ideal_x,
        ideal_y,
        linestyle="--",
        color="#808080",
        linewidth=1.2,
        label="idealno O(n)",
        zorder=2,
    )

    ax.plot(
        vertices,
        nanoseconds,
        marker="o",
        color=SINGLE_SERIES_COLOR,
        linewidth=1.6,
        label="izmjereno",
        zorder=3,
    )

    ax.set_xlabel("Broj vrhova poligona")
    ax.set_ylabel("Prosječno vrijeme poziva [ns]")
    ax.grid(True, which="major", alpha=0.3)
    ax.grid(True, which="minor", alpha=0.15)

    y_bottom, y_top = ax.get_ylim()
    ax.text(
        math.sqrt(low * high),
        y_bottom * (y_top / y_bottom) ** 0.04,
        "tipičan raspon",
        ha="center",
        va="bottom",
        fontsize=9,
        color=SINGLE_SERIES_COLOR,
    )

    worst_vertices = int(vertices[-1])
    worst_ns = float(nanoseconds[-1])
    core_pct = worst_ns * GEOFENCE_HZ / 1e9 * 100.0
    ax.annotate(
        f"pri {worst_vertices} vrhova i {GEOFENCE_HZ:g} Hz:\n~{fmt_hr(core_pct, 4)} % jedne jezgre",
        xy=(vertices[-1], nanoseconds[-1]),
        xytext=(0.97, 0.06),
        textcoords="axes fraction",
        ha="right",
        va="bottom",
        fontsize=9,
        arrowprops={"arrowstyle": "->", "color": "#444444", "linewidth": 0.9},
        bbox={"boxstyle": "round,pad=0.35", "facecolor": "white", "edgecolor": "#BBBBBB", "alpha": 0.9},
    )
    print(
        f"[podatak] G9 — {worst_vertices} vrhova: {worst_ns:.0f} ns po pozivu "
        f"→ {core_pct:.4f} % jedne jezgre pri {GEOFENCE_HZ:g} Hz"
    )

    ax.legend(loc="upper left")
    save_fig(fig, "g9_slozenost_contains.png")


def _draw_polygon(ax, outline: np.ndarray) -> None:
    ax.plot(outline[:, 0], outline[:, 1], color="black", linewidth=1.4, label="Granica poligona", zorder=4)


def _route_xy(route: pd.DataFrame) -> np.ndarray:
    return route[["east_m", "north_m"]].to_numpy(dtype=float)


def _finish_map_axes(ax) -> None:
    ax.set_aspect("equal")
    ax.set_xlabel("Istok [m]")
    ax.set_ylabel("Sjever [m]")
    ax.grid(True, alpha=0.3)


def graph_g7() -> None:
    polygons = load_csv("polygons.csv")
    if polygons is None:
        print("[preskočeno] G7 (crtež rute preko poligona) — nedostaje polygons.csv")
        return

    for polygon in MAP_POLYGONS:
        outline = polygon_outline(polygons, polygon)
        if outline is None:
            print(f"[preskočeno] G7 {polygon} — nema vrhova poligona u polygons.csv")
            continue

        routes: dict[str, np.ndarray] = {}
        for strategy in STRATEGY_ORDER:
            route = load_route(polygon, strategy)
            if route is None:
                print(f"[preskočeno] G7 {polygon}/{strategy} — nema datoteke rute")
                continue
            xy = _route_xy(route)
            routes[strategy] = xy

            fig, ax = plt.subplots(figsize=(5.6, 5.6))
            _draw_polygon(ax, outline)
            ax.plot(
                xy[:, 0],
                xy[:, 1],
                color=STRATEGY_COLORS.get(strategy, "#999999"),
                linewidth=0.8,
                label=STRATEGY_LABELS.get(strategy, strategy),
                zorder=3,
            )
            ax.plot(
                xy[0, 0],
                xy[0, 1],
                marker="o",
                markersize=8,
                markerfacecolor=START_MARKER_COLOR,
                markeredgecolor="black",
                markeredgewidth=0.8,
                linestyle="none",
                label="Početak",
                zorder=5,
            )
            set_map_limits(ax, outline, [xy])
            _finish_map_axes(ax)
            ax.legend(loc="upper center", bbox_to_anchor=(0.5, -0.12), ncol=2, frameon=False)
            save_fig(fig, f"g7_{polygon}_{STRATEGY_SLUGS.get(strategy, strategy.lower())}.png")

        if not routes:
            print(f"[preskočeno] G7 {polygon} usporedba — nijedna ruta nije učitana")
            continue

        fig, ax = plt.subplots(figsize=(5.6, 5.6))
        _draw_polygon(ax, outline)
        start_labelled = False
        for strategy in STRATEGY_ORDER:
            xy = routes.get(strategy)
            if xy is None:
                continue
            ax.plot(
                xy[:, 0],
                xy[:, 1],
                color=STRATEGY_COLORS.get(strategy, "#999999"),
                linewidth=0.7,
                alpha=0.85,
                label=STRATEGY_LABELS.get(strategy, strategy),
                zorder=3,
            )
            ax.plot(
                xy[0, 0],
                xy[0, 1],
                marker="o",
                markersize=7,
                markerfacecolor=START_MARKER_COLOR,
                markeredgecolor="black",
                markeredgewidth=0.8,
                linestyle="none",
                label=None if start_labelled else "Početak",
                zorder=5,
            )
            start_labelled = True
        set_map_limits(ax, outline, list(routes.values()))
        _finish_map_axes(ax)
        ax.legend(loc="upper center", bbox_to_anchor=(0.5, -0.12), ncol=2, frameon=False)
        save_fig(fig, f"g7_{polygon}_usporedba.png")


def _coverage_annotation(coverage: pd.DataFrame | None, polygon: str, strategy: str, swath: float | None) -> str | None:

    if coverage is None:
        return None
    sub = coverage[(coverage["polygon_id"] == polygon) & (coverage["strategy"] == strategy)]
    sub = sub[np.isclose(sub["spacing_m"].astype(float), MAP_SPACING)]
    if swath is not None and "swath_m" in sub.columns:
        sub = sub[np.isclose(sub["swath_m"].astype(float), swath)]
    if sub.empty:
        print(f"[upozorenje] G8 {polygon}/{strategy} — nema odgovarajućeg retka u coverage.csv, natpis se izostavlja")
        return None
    row = sub.iloc[0]
    return (
        f"pokrivenost {fmt_hr(float(row['coverage_pct']))} %\n"
        f"preklapanje {fmt_hr(float(row['overlap_pct']))} %\n"
        f"izvan poligona {fmt_hr(float(row['outside_pct']))} %"
    )


def graph_g8() -> None:
    coverage = load_csv("coverage.csv")
    polygons = load_csv("polygons.csv")

    colors = [color for _, color, _ in RASTER_STATES]
    cmap = ListedColormap(colors)
    norm = BoundaryNorm(np.arange(-0.5, len(colors) + 0.5, 1.0), cmap.N)
    labels = {value: label for value, _, label in RASTER_STATES}
    color_by_value = {value: color for value, color, _ in RASTER_STATES}
    handles = [
        Patch(facecolor=color_by_value[v], edgecolor="#777777", label=labels[v]) for v in RASTER_LEGEND_ORDER
    ]

    for polygon in MAP_POLYGONS:
        outline = polygon_outline(polygons, polygon) if polygons is not None else None
        for strategy in STRATEGY_ORDER:
            loaded = load_raster(polygon, strategy)
            if loaded is None:
                print(f"[preskočeno] G8 {polygon}/{strategy} — nema rasterske datoteke")
                continue
            meta, grid = loaded

            cell = _meta_float(meta, "cell_m")
            min_east = _meta_float(meta, "min_east_m")
            min_north = _meta_float(meta, "min_north_m")
            if cell is None or min_east is None or min_north is None:
                print(f"[preskočeno] G8 {polygon}/{strategy} — nepotpuni metapodaci rastera")
                continue
            ny, nx = grid.shape
            extent = (min_east, min_east + nx * cell, min_north, min_north + ny * cell)

            slot_of = {value: slot for slot, (value, _, _) in enumerate(RASTER_STATES)}
            index = np.full(grid.shape, slot_of[0], dtype=int)
            for value in (-2, -1, 0, 1):
                index[grid == value] = slot_of[value]
            index[grid >= 2] = slot_of[2]

            fig, ax = plt.subplots(figsize=(5.8, 5.8))
            ax.imshow(
                index,
                cmap=cmap,
                norm=norm,
                origin="lower",
                extent=extent,
                interpolation="nearest",
            )
            if outline is not None:
                ax.plot(outline[:, 0], outline[:, 1], color="black", linewidth=1.0, zorder=3)
            _finish_map_axes(ax)
            ax.grid(False)

            text = _coverage_annotation(coverage, polygon, strategy, _meta_float(meta, "swath_m"))
            if text:
                ax.text(
                    0.02,
                    0.02,
                    text,
                    transform=ax.transAxes,
                    ha="left",
                    va="bottom",
                    fontsize=8.5,
                    bbox={"boxstyle": "round,pad=0.3", "facecolor": "white", "edgecolor": "#BBBBBB", "alpha": 0.88},
                    zorder=6,
                )

            ax.legend(
                handles=handles,
                loc="upper center",
                bbox_to_anchor=(0.5, -0.12),
                ncol=2,
                frameon=False,
                fontsize=9,
            )
            save_fig(fig, f"g8_{polygon}_{STRATEGY_SLUGS.get(strategy, strategy.lower())}.png")


def graph_g10() -> None:
    polygon, strategy, spacing, sigma = "P1", "Boustrophedon", MAP_SPACING, 0.084

    traces_dir = RESULTS_DIR / "traces"
    pattern = f"trace_{polygon}_{strategy}_{spacing:.2f}_{sigma:.3f}_*.csv"
    matches = sorted(traces_dir.glob(pattern)) if traces_dir.is_dir() else []
    if not matches:
        print(f"[preskočeno] G10 (planirano vs stvarno) — nema datoteke traga: results/traces/{pattern}")
        return
    trace_path = matches[0]
    print(f"[podatak] G10 — odabrani trag: {trace_path.relative_to(REPO_ROOT)}")

    try:
        trace = pd.read_csv(trace_path)
    except Exception as exc:
        print(f"[preskočeno] G10 — greška pri čitanju {trace_path.relative_to(REPO_ROOT)}: {exc}")
        return
    if trace.empty:
        print(f"[preskočeno] G10 — datoteka traga je prazna: {trace_path.relative_to(REPO_ROOT)}")
        return

    route = load_route(polygon, strategy, spacing)
    if route is None:
        print("[preskočeno] G10 — nedostaje planirana ruta (results/routes/)")
        return

    lat0 = float(trace["true_lat"].iloc[0])
    lon0 = float(trace["true_lon"].iloc[0])

    if {"lat", "lon"}.issubset(route.columns):
        route_e, route_n = project_latlon(route["lat"], route["lon"], lat0, lon0)
    else:
        print("[upozorenje] G10 — ruta nema lat/lon stupce, koriste se east_m/north_m (moguć pomak okvira)")
        route_e = route["east_m"].to_numpy(dtype=float)
        route_n = route["north_m"].to_numpy(dtype=float)

    true_e, true_n = project_latlon(trace["true_lat"], trace["true_lon"], lat0, lon0)
    rep_e, rep_n = project_latlon(trace["reported_lat"], trace["reported_lon"], lat0, lon0)

    def draw_layers(target, lw_scale: float = 1.0) -> None:
        target.plot(
            rep_e,
            rep_n,
            color="#CC79A7",
            linewidth=0.4 * lw_scale,
            alpha=0.45,
            label="Prijavljena GPS pozicija",
            zorder=2,
        )
        target.plot(
            route_e,
            route_n,
            color="#444444",
            linewidth=0.9 * lw_scale,
            linestyle="--",
            label="Planirana ruta",
            zorder=3,
        )
        target.plot(
            true_e,
            true_n,
            color=STRATEGY_COLORS["Boustrophedon"],
            linewidth=1.0 * lw_scale,
            label="Stvarna pozicija",
            zorder=4,
        )

    fig, ax = plt.subplots(figsize=(6.4, 6.4))
    draw_layers(ax)
    _finish_map_axes(ax)

    try:
        x0 = float(np.min(route_e))
        y0 = float(np.min(route_n))
        span = 2.0
        inset = ax.inset_axes([1.08, 0.54, 0.46, 0.46])
        draw_layers(inset, lw_scale=1.6)
        inset.set_xlim(x0 - 0.4, x0 - 0.4 + span)
        inset.set_ylim(y0 - 0.2, y0 - 0.2 + span)
        inset.set_aspect("equal")
        inset.tick_params(labelsize=8)
        inset.grid(True, alpha=0.3)
        ax.indicate_inset_zoom(inset, edgecolor="#444444", alpha=0.9, linewidth=1.0)
    except Exception as exc:
        print(f"[upozorenje] G10 — povećani izrez nije nacrtan: {exc}")

    handles, labels = ax.get_legend_handles_labels()
    order = ["Planirana ruta", "Stvarna pozicija", "Prijavljena GPS pozicija"]
    by_label = dict(zip(labels, handles))
    ordered = [(by_label[name], name) for name in order if name in by_label]
    ax.legend(
        [h for h, _ in ordered],
        [n for _, n in ordered],
        loc="upper center",
        bbox_to_anchor=(0.5, -0.1),
        ncol=3,
        frameon=False,
    )
    save_fig(fig, "g10_planirano_vs_stvarno.png")


def graph_g12() -> None:
    df = load_csv("equal_coverage.csv")
    if df is None:
        print("[preskočeno] G12 (radna učinkovitost) — nedostaje equal_coverage.csv")
        return

    needed = {"polygon_id", "strategy", "work_efficiency_pct"}
    missing = needed - set(df.columns)
    if missing:
        print(f"[preskočeno] G12 — nedostaju stupci: {sorted(missing)}")
        return

    polygons = sorted(df["polygon_id"].dropna().unique(), key=_polygon_sort_key)
    strategies = [s for s in STRATEGY_ORDER if s in set(df["strategy"])]
    if not polygons or not strategies:
        print("[preskočeno] G12 — nema podataka za crtanje")
        return

    x = np.arange(len(polygons))
    width = 0.8 / max(1, len(strategies))

    fig, ax = plt.subplots(figsize=(8, 4.5))
    for i, strat in enumerate(strategies):
        s = df[df["strategy"] == strat].set_index("polygon_id")
        values = [s.loc[p, "work_efficiency_pct"] if p in s.index else np.nan for p in polygons]
        ax.bar(
            x + (i - (len(strategies) - 1) / 2) * width,
            values,
            width=width * 0.95,
            label=STRATEGY_LABELS.get(strat, strat),
            color=STRATEGY_COLORS.get(strat, SINGLE_SERIES_COLOR),
        )

    ax.axhline(80, color="black", ls="--", lw=1.3)
    ax.text(len(polygons) - 0.5, 80, " 80 % (sustavno, [4])", va="bottom", ha="right", fontsize=8.5, color="black", fontweight="bold")
    ax.axhline(35, color="black", ls="--", lw=1.3)
    ax.text(len(polygons) - 0.5, 35, " 35 % (nasumično, [4])", va="bottom", ha="right", fontsize=8.5, color="black", fontweight="bold")

    ax.set_xticks(x)
    ax.set_xticklabels(polygons)
    ax.set_xlabel("Poligon")
    ax.set_ylabel("Radna učinkovitost [%]")
    ax.grid(True, axis="y", alpha=0.3)
    ax.legend(fontsize=8)
    save_fig(fig, "g12_radna_ucinkovitost.png")


def graph_g13() -> None:
    df = load_csv("geofence_latency.csv")
    if df is None:
        print("[preskočeno] G13 (geofence M1) — nedostaje geofence_latency.csv")
        return

    needed = {"speed_ms", "sigma_m", "distance_past_boundary_m"}
    missing = needed - set(df.columns)
    if missing:
        print(f"[preskočeno] G13 — nedostaju stupci: {sorted(missing)}")
        return

    speeds = sorted(df["speed_ms"].dropna().unique())
    sigmas = sorted(df["sigma_m"].dropna().unique())
    if not speeds or not sigmas:
        print("[preskočeno] G13 — nema podataka o brzini/šumu")
        return

    x = np.arange(len(speeds))
    width = 0.8 / max(1, len(sigmas))

    fig, ax = plt.subplots(figsize=(6.5, 4))
    for i, sigma in enumerate(sigmas):
        means, stds = [], []
        for speed in speeds:
            s = df[(df["speed_ms"] == speed) & (df["sigma_m"] == sigma)]["distance_past_boundary_m"].dropna()
            means.append(s.mean() if not s.empty else np.nan)
            stds.append(s.std() if len(s) > 1 else 0.0)
        color = SIGMA_COLORS[i % len(SIGMA_COLORS)]
        ax.bar(
            x + (i - (len(sigmas) - 1) / 2) * width,
            means,
            width=width * 0.95,
            yerr=stds,
            capsize=3,
            label=f"σ = {sigma:g} m",
            color=color,
        )

    ax.set_xticks(x)
    ax.set_xticklabels([f"{v:g}" for v in speeds])
    ax.set_xlabel("Brzina vožnje [m/s]")
    ax.set_ylabel("Put preko granice do zaustavljanja [m]")
    ax.grid(True, axis="y", alpha=0.3)
    ax.legend(fontsize=8)
    save_fig(fig, "g13_geofence_kasnjenje.png")


def graph_g14() -> None:
    df = load_csv("route_simplification.csv")
    if df is None:
        print("[preskočeno] G14 (produkcijski put) — nedostaje route_simplification.csv")
        return

    needed = {"polygon_id", "strategy", "points_full", "points_reduced", "coverage_full_pct", "coverage_reduced_pct"}
    missing = needed - set(df.columns)
    if missing:
        print(f"[preskočeno] G14 — nedostaju stupci: {sorted(missing)}")
        return

    polygons = sorted(df["polygon_id"].dropna().unique(), key=_polygon_sort_key)
    strategies = [s for s in STRATEGY_ORDER if s in set(df["strategy"])]
    if not polygons or not strategies:
        print("[preskočeno] G14 — nema podataka za crtanje")
        return

    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(11, 4.2))
    x = np.arange(len(polygons))
    width = 0.8 / max(1, len(strategies))

    for i, strat in enumerate(strategies):
        s = df[df["strategy"] == strat].set_index("polygon_id")
        full = [s.loc[p, "points_full"] if p in s.index else np.nan for p in polygons]
        reduced = [s.loc[p, "points_reduced"] if p in s.index else np.nan for p in polygons]
        color = STRATEGY_COLORS.get(strat, SINGLE_SERIES_COLOR)
        off = (i - (len(strategies) - 1) / 2) * width
        ax1.bar(x + off, full, width=width * 0.95, color=color, alpha=0.35)
        ax1.bar(x + off, reduced, width=width * 0.95, color=color, label=STRATEGY_LABELS.get(strat, strat))

        cov_full = [s.loc[p, "coverage_full_pct"] if p in s.index else np.nan for p in polygons]
        cov_reduced = [s.loc[p, "coverage_reduced_pct"] if p in s.index else np.nan for p in polygons]
        ax2.plot(x, cov_full, "o", color=color, alpha=0.35, markersize=9)
        ax2.plot(x, cov_reduced, "x", color=color, markersize=7,
                 label=STRATEGY_LABELS.get(strat, strat))

    ax1.set_xticks(x)
    ax1.set_xticklabels(polygons)
    ax1.set_xlabel("Poligon")
    ax1.set_ylabel("Broj točaka rute\n(blijedo = puna ruta, puno = nakon uklanjanja)")
    ax1.set_yscale("log")
    ax1.grid(True, axis="y", alpha=0.3)
    ax1.legend(fontsize=8)

    ax2.set_xticks(x)
    ax2.set_xticklabels(polygons)
    ax2.set_xlabel("Poligon")
    ax2.set_ylabel("Pokrivenost [%]\n(○ = puna ruta, × = nakon uklanjanja)")
    ax2.grid(True, axis="y", alpha=0.3)
    ax2.legend(fontsize=8)

    fig.tight_layout()
    save_fig(fig, "g14_produkcijski_put.png")


def main() -> None:
    FIGURES_DIR.mkdir(parents=True, exist_ok=True)
    print(f"Direktorij s rezultatima: {RESULTS_DIR}")
    print(f"Direktorij za slike:      {FIGURES_DIR}")
    print()

    safe_graph("G1", graph_g1)
    safe_graph("G2", graph_g2)
    safe_graph("G3", graph_g3)
    safe_graph("G4a", graph_g4a)
    safe_graph("G4b", graph_g4b)
    safe_graph("G5", graph_g5)
    safe_graph("G6", graph_g6)
    safe_graph("G11", graph_g11)
    safe_graph("G7", graph_g7)
    safe_graph("G8", graph_g8)
    safe_graph("G9", graph_g9)
    safe_graph("G10", graph_g10)
    safe_graph("G12", graph_g12)
    safe_graph("G13", graph_g13)
    safe_graph("G14", graph_g14)


if __name__ == "__main__":
    main()

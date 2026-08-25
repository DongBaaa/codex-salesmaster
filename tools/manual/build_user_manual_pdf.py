from __future__ import annotations

import argparse
import hashlib
import html
import io
import json
import re
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image as PILImage
from pypdf import PageObject, PdfReader, PdfWriter
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas as pdf_canvas
from reportlab.platypus import (
    BaseDocTemplate,
    CondPageBreak,
    Frame,
    Image,
    KeepTogether,
    PageBreak,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
)
from reportlab.platypus.tableofcontents import TableOfContents


DOC_DATE = "2026-08-22"
CAPTURE_EVIDENCE_KIND = "georaeplan-current-wpf-exact-matrix-v2"
EXPECTED_CAPTURE_RESULT_SHA256 = "6182B6A19A67D7976E27A1C1EF5D39EA27E471111F7C3C67D752B92DFDE2CCC5"
EXPECTED_CAPTURE_ASSEMBLY_SHA256 = "C1DD126443642E9D882CCE0693D8EF23F4843D30D50BE23205223EB74E0CE493"
EXPECTED_CAPTURE_MEASUREMENT_COUNT = 768
EXPECTED_CAPTURE_SCREENSHOT_COUNT = 36

PROJECT_ROOT: Path
SCREENSHOT_DIR: Path
OUTPUT_DIR: Path
OUTPUT_PATH: Path
REQUESTED_PATH: Path
VERIFICATION_PATH: Path
LOCAL_DESKTOP_VERSION: str
LOCAL_DESKTOP_FILE_VERSION: str
PUBLIC_STABLE_DESKTOP_VERSION: str
ANDROID_VERSION: str
ANDROID_VERSION_CODE: str
PUBLIC_STABLE_ANDROID_VERSION: str
PUBLIC_STABLE_ANDROID_FILENAME: str
CAPTURE_DATE: str
CAPTURE_DESKTOP_VERSION: str
CAPTURE_RESULT_SHA256: str
CAPTURE_ASSEMBLY_SHA256: str
CAPTURE_MEASUREMENT_COUNT: int
CAPTURE_SUCCESS_SCREENSHOT_COUNT: int
CAPTURE_MODELLED_MEASUREMENT_COUNT: int
SCREENSHOT_FILES: tuple[str, ...]


FONT_REGULAR = "MalgunGothic"
FONT_BOLD = "MalgunGothic-Bold"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="거래플랜 사용자 메뉴얼 PDF를 생성하고 구조·텍스트를 검증합니다."
    )
    parser.add_argument(
        "--project-root",
        type=Path,
        help="거래플랜 저장소 루트. 생략하면 스크립트 위치에서 자동 탐색합니다.",
    )
    return parser.parse_args()


def resolve_project_root(explicit_root: Path | None) -> Path:
    if explicit_root is not None:
        candidate = explicit_root.expanduser().resolve()
        if not (candidate / "README.md").is_file():
            raise FileNotFoundError(f"저장소 루트의 README.md를 찾을 수 없습니다: {candidate}")
        return candidate

    for candidate in Path(__file__).resolve().parents:
        git_marker = candidate / ".git"
        if (candidate / "README.md").is_file() and git_marker.exists():
            return candidate

    raise FileNotFoundError("스크립트 위치에서 거래플랜 저장소 루트를 찾을 수 없습니다.")


def read_project_property(project_path: Path, property_name: str) -> str:
    root = ET.parse(project_path).getroot()
    matches = [
        (element.text or "").strip()
        for element in root.iter()
        if element.tag.rsplit("}", 1)[-1] == property_name
        and (element.text or "").strip()
    ]
    if len(matches) != 1:
        raise ValueError(
            f"{project_path}의 {property_name} 속성은 정확히 하나여야 합니다: {len(matches)}개"
        )
    return matches[0]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load_capture_manifest(manifest_path: Path, screenshot_dir: Path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != 2:
        raise ValueError("캡처 manifest schemaVersion은 2여야 합니다.")

    source_evidence = manifest.get("sourceEvidence")
    expected_evidence_keys = {
        "kind",
        "resultSha256",
        "assemblySha256",
        "measurementCount",
        "successScreenshotCount",
        "modelledMeasurementCount",
    }
    if not isinstance(source_evidence, dict) or set(source_evidence) != expected_evidence_keys:
        raise ValueError("캡처 sourceEvidence 스키마가 정확하지 않습니다.")
    if source_evidence.get("kind") != CAPTURE_EVIDENCE_KIND:
        raise ValueError("캡처 sourceEvidence kind가 현재 WPF exact 계약과 다릅니다.")
    if source_evidence.get("resultSha256") != EXPECTED_CAPTURE_RESULT_SHA256:
        raise ValueError("캡처 exact 결과 SHA-256이 고정된 현재 증거와 다릅니다.")
    if source_evidence.get("assemblySha256") != EXPECTED_CAPTURE_ASSEMBLY_SHA256:
        raise ValueError("캡처 실행 어셈블리 SHA-256이 고정된 현재 증거와 다릅니다.")
    if source_evidence.get("measurementCount") != EXPECTED_CAPTURE_MEASUREMENT_COUNT:
        raise ValueError("캡처 exact 측정 수는 768이어야 합니다.")
    if source_evidence.get("successScreenshotCount") != EXPECTED_CAPTURE_SCREENSHOT_COUNT:
        raise ValueError("캡처 exact 성공 화면 수는 36이어야 합니다.")
    if source_evidence.get("modelledMeasurementCount") != 0:
        raise ValueError("캡처 exact 증거에는 모델링 측정이 없어야 합니다.")

    screenshots = manifest.get("screenshots")
    if not isinstance(screenshots, list) or len(screenshots) != 15:
        raise ValueError("캡처 manifest에는 정확히 15개 스크린샷이 있어야 합니다.")

    file_names: set[str] = set()
    source_windows: set[str] = set()
    screenshot_hashes: set[str] = set()
    for entry in screenshots:
        if not isinstance(entry, dict) or set(entry) != {"fileName", "sourceWindow", "sha256"}:
            raise ValueError(f"잘못된 캡처 manifest 항목입니다: {entry!r}")
        file_name = entry.get("fileName")
        source_window = entry.get("sourceWindow")
        expected_hash = entry.get("sha256")
        if (
            not isinstance(file_name, str)
            or Path(file_name).name != file_name
            or not isinstance(source_window, str)
            or re.fullmatch(r"[A-Za-z][A-Za-z0-9]*Window", source_window) is None
            or not isinstance(expected_hash, str)
            or re.fullmatch(r"[0-9A-F]{64}", expected_hash) is None
        ):
            raise ValueError(f"잘못된 캡처 manifest 항목입니다: {entry!r}")
        if file_name in file_names or source_window in source_windows or expected_hash in screenshot_hashes:
            raise ValueError(f"중복된 캡처 manifest 항목입니다: {entry!r}")
        file_names.add(file_name)
        source_windows.add(source_window)
        screenshot_hashes.add(expected_hash)

        screenshot_path = screenshot_dir / file_name
        if not screenshot_path.is_file():
            raise FileNotFoundError(f"캡처 파일을 찾을 수 없습니다: {screenshot_path}")
        actual_hash = sha256_file(screenshot_path)
        if actual_hash != expected_hash.upper():
            raise ValueError(
                f"캡처 SHA-256이 manifest와 다릅니다: {file_name} "
                f"expected={expected_hash.upper()} actual={actual_hash}"
            )

    return manifest


def configure(project_root: Path) -> None:
    global PROJECT_ROOT
    global SCREENSHOT_DIR
    global OUTPUT_DIR
    global OUTPUT_PATH
    global REQUESTED_PATH
    global VERIFICATION_PATH
    global LOCAL_DESKTOP_VERSION
    global LOCAL_DESKTOP_FILE_VERSION
    global PUBLIC_STABLE_DESKTOP_VERSION
    global ANDROID_VERSION
    global ANDROID_VERSION_CODE
    global PUBLIC_STABLE_ANDROID_VERSION
    global PUBLIC_STABLE_ANDROID_FILENAME
    global CAPTURE_DATE
    global CAPTURE_DESKTOP_VERSION
    global CAPTURE_RESULT_SHA256
    global CAPTURE_ASSEMBLY_SHA256
    global CAPTURE_MEASUREMENT_COUNT
    global CAPTURE_SUCCESS_SCREENSHOT_COUNT
    global CAPTURE_MODELLED_MEASUREMENT_COUNT
    global SCREENSHOT_FILES

    PROJECT_ROOT = project_root
    asset_root = Path(__file__).resolve().parent / "assets"
    SCREENSHOT_DIR = asset_root / "screenshots"
    OUTPUT_DIR = PROJECT_ROOT / "output" / "pdf"
    OUTPUT_PATH = OUTPUT_DIR / "거래플랜 사용자 메뉴얼.pdf"
    REQUESTED_PATH = PROJECT_ROOT / "거래플랜 사용자 메뉴얼.pdf"
    VERIFICATION_PATH = OUTPUT_DIR / "georaeplan-user-manual.verification.json"

    desktop_project = PROJECT_ROOT.joinpath(
        "Desktop",
        "거래플랜.Desktop.App",
        "거래플랜.Desktop.App.csproj",
    )
    mobile_project = PROJECT_ROOT.joinpath(
        "Mobile",
        "GeoraePlan.Mobile.App",
        "GeoraePlan.Mobile.App.csproj",
    )
    stable_manifest_path = PROJECT_ROOT.joinpath("배포", "stable.json")
    stable_manifest = json.loads(stable_manifest_path.read_text(encoding="utf-8-sig"))

    LOCAL_DESKTOP_VERSION = read_project_property(desktop_project, "Version")
    LOCAL_DESKTOP_FILE_VERSION = read_project_property(desktop_project, "FileVersion")
    PUBLIC_STABLE_DESKTOP_VERSION = str(stable_manifest["desktop"]["version"])
    ANDROID_VERSION = read_project_property(
        mobile_project,
        "ApplicationDisplayVersion",
    )
    ANDROID_VERSION_CODE = read_project_property(
        mobile_project,
        "ApplicationVersion",
    )
    PUBLIC_STABLE_ANDROID_VERSION = str(stable_manifest["android"]["version"])
    PUBLIC_STABLE_ANDROID_FILENAME = str(stable_manifest["android"]["fileName"])

    capture_manifest = load_capture_manifest(
        asset_root / "capture-manifest.json",
        SCREENSHOT_DIR,
    )
    CAPTURE_DATE = str(capture_manifest["captureDate"])
    CAPTURE_DESKTOP_VERSION = str(capture_manifest["desktopVersion"])
    capture_evidence = capture_manifest["sourceEvidence"]
    CAPTURE_RESULT_SHA256 = str(capture_evidence["resultSha256"])
    CAPTURE_ASSEMBLY_SHA256 = str(capture_evidence["assemblySha256"])
    CAPTURE_MEASUREMENT_COUNT = int(capture_evidence["measurementCount"])
    CAPTURE_SUCCESS_SCREENSHOT_COUNT = int(capture_evidence["successScreenshotCount"])
    CAPTURE_MODELLED_MEASUREMENT_COUNT = int(capture_evidence["modelledMeasurementCount"])
    if CAPTURE_DATE != DOC_DATE:
        raise ValueError(
            "화면 캡처 날짜는 문서 기능 기준일과 같아야 합니다: "
            f"document={DOC_DATE} capture={CAPTURE_DATE}"
        )
    if CAPTURE_DESKTOP_VERSION != LOCAL_DESKTOP_VERSION:
        raise ValueError(
            "화면 캡처 Desktop 버전은 현재 소스 버전과 같아야 합니다: "
            f"source={LOCAL_DESKTOP_VERSION} capture={CAPTURE_DESKTOP_VERSION}"
        )
    SCREENSHOT_FILES = tuple(
        str(entry["fileName"])
        for entry in capture_manifest["screenshots"]
    )


def register_fonts() -> None:
    regular_path = Path(r"C:\Windows\Fonts\malgun.ttf")
    bold_path = Path(r"C:\Windows\Fonts\malgunbd.ttf")
    if not regular_path.exists() or not bold_path.exists():
        raise FileNotFoundError("맑은 고딕 폰트를 찾을 수 없습니다. Windows 기본 폰트 설치 상태를 확인하세요.")

    pdfmetrics.registerFont(TTFont(FONT_REGULAR, str(regular_path)))
    pdfmetrics.registerFont(TTFont(FONT_BOLD, str(bold_path)))
    pdfmetrics.registerFontFamily(
        FONT_REGULAR,
        normal=FONT_REGULAR,
        bold=FONT_BOLD,
        italic=FONT_REGULAR,
        boldItalic=FONT_BOLD,
    )


class ManualDocTemplate(BaseDocTemplate):
    def __init__(self, filename: str, **kwargs):
        super().__init__(filename, **kwargs)
        frame = Frame(
            self.leftMargin,
            self.bottomMargin,
            self.width,
            self.height,
            id="normal",
            showBoundary=0,
        )
        self.addPageTemplates(
            [
                PageTemplate(
                    id="manual",
                    frames=[frame],
                )
            ]
        )

    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph):
            style_name = flowable.style.name
            if style_name == "ManualHeading1":
                self.notify("TOCEntry", (0, flowable.getPlainText(), self.page))
            elif style_name == "ManualHeading2":
                self.notify("TOCEntry", (1, flowable.getPlainText(), self.page))


def draw_header_footer(canvas, left: float, right: float, page_number: int) -> None:
    canvas.saveState()
    width, height = A4
    top = height - 16 * mm
    bottom = 12 * mm

    canvas.setStrokeColor(colors.HexColor("#D7DEE8"))
    canvas.setLineWidth(0.4)
    canvas.line(left, top, right, top)

    # ReportLab's multi-pass TOC build can hide Korean running text on
    # alternating pages. Use the built-in font for the fixed running labels so
    # every rendered page has the same visible header and footer.
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(colors.HexColor("#64748B"))
    canvas.drawString(left, top + 4, "GeoraePlan User Manual")
    canvas.drawRightString(right, top + 4, f"Updated {DOC_DATE}")
    canvas.drawRightString(right, bottom, str(page_number))
    canvas.drawString(left, bottom, "User and maintainer operations guide")
    canvas.restoreState()


def stamp_header_footer(path: Path, left_margin: float, right_margin: float) -> None:
    reader = PdfReader(path)
    writer = PdfWriter()
    right = A4[0] - right_margin

    for page_number, page in enumerate(reader.pages, start=1):
        overlay_buffer = io.BytesIO()
        overlay_canvas = pdf_canvas.Canvas(overlay_buffer, pagesize=A4)
        draw_header_footer(overlay_canvas, left_margin, right, page_number)
        overlay_canvas.save()
        overlay_buffer.seek(0)
        overlay_page = PdfReader(overlay_buffer).pages[0]
        composed_page = PageObject.create_blank_page(
            width=float(page.mediabox.width),
            height=float(page.mediabox.height),
        )
        composed_page.merge_page(page, over=True)
        composed_page.merge_page(overlay_page, over=True)
        writer.add_page(composed_page)

    if reader.metadata:
        writer.add_metadata(
            {
                key: str(value)
                for key, value in reader.metadata.items()
                if value is not None
            }
        )

    stamped_path = path.with_suffix(".stamped.pdf")
    with stamped_path.open("wb") as output:
        writer.write(output)
    stamped_path.replace(path)


def make_styles():
    base = getSampleStyleSheet()
    styles = {}
    styles["Title"] = ParagraphStyle(
        "ManualTitle",
        parent=base["Title"],
        fontName=FONT_BOLD,
        fontSize=25,
        leading=31,
        alignment=TA_CENTER,
        textColor=colors.HexColor("#0F172A"),
        spaceAfter=12,
    )
    styles["Subtitle"] = ParagraphStyle(
        "ManualSubtitle",
        parent=base["Normal"],
        fontName=FONT_REGULAR,
        fontSize=11,
        leading=16,
        alignment=TA_CENTER,
        textColor=colors.HexColor("#334155"),
        spaceAfter=14,
    )
    styles["Heading1"] = ParagraphStyle(
        "ManualHeading1",
        parent=base["Heading1"],
        fontName=FONT_BOLD,
        fontSize=16,
        leading=21,
        textColor=colors.HexColor("#0F3B66"),
        spaceBefore=8,
        spaceAfter=8,
        keepWithNext=True,
    )
    styles["Heading2"] = ParagraphStyle(
        "ManualHeading2",
        parent=base["Heading2"],
        fontName=FONT_BOLD,
        fontSize=12.5,
        leading=17,
        textColor=colors.HexColor("#0F3B66"),
        spaceBefore=7,
        spaceAfter=5,
        keepWithNext=True,
    )
    styles["Heading3"] = ParagraphStyle(
        "ManualHeading3",
        parent=base["Heading3"],
        fontName=FONT_BOLD,
        fontSize=10.6,
        leading=14,
        textColor=colors.HexColor("#1E293B"),
        spaceBefore=5,
        spaceAfter=3,
        keepWithNext=True,
    )
    styles["Body"] = ParagraphStyle(
        "ManualBody",
        parent=base["BodyText"],
        fontName=FONT_REGULAR,
        fontSize=9.2,
        leading=13.4,
        alignment=TA_LEFT,
        textColor=colors.HexColor("#1F2937"),
        spaceAfter=3.5,
    )
    styles["Small"] = ParagraphStyle(
        "ManualSmall",
        parent=styles["Body"],
        fontSize=8.1,
        leading=11.5,
        textColor=colors.HexColor("#475569"),
        spaceAfter=2.5,
    )
    styles["Caption"] = ParagraphStyle(
        "ManualCaption",
        parent=styles["Small"],
        alignment=TA_CENTER,
        textColor=colors.HexColor("#475569"),
        spaceBefore=2,
        spaceAfter=8,
    )
    styles["TableHead"] = ParagraphStyle(
        "ManualTableHead",
        parent=styles["Small"],
        fontName=FONT_BOLD,
        textColor=colors.HexColor("#0F172A"),
        leading=11.5,
    )
    styles["TableBody"] = ParagraphStyle(
        "ManualTableBody",
        parent=styles["Small"],
        textColor=colors.HexColor("#1F2937"),
        leading=11.5,
    )
    styles["Note"] = ParagraphStyle(
        "ManualNote",
        parent=styles["Small"],
        leading=12.2,
        textColor=colors.HexColor("#0F172A"),
    )
    styles["TocTitle"] = ParagraphStyle(
        "ManualTocTitle",
        parent=styles["Heading1"],
        spaceBefore=0,
    )
    return styles


def esc(value: object) -> str:
    return html.escape(str(value), quote=False)


def p(text: str, style: ParagraphStyle) -> Paragraph:
    return Paragraph(esc(text).replace("\n", "<br/>"), style)


def rich(html_text: str, style: ParagraphStyle) -> Paragraph:
    return Paragraph(html_text, style)


def add_heading(story: list, styles, level: int, text: str) -> None:
    style = styles["Heading1"] if level == 1 else styles["Heading2"] if level == 2 else styles["Heading3"]
    story.append(Paragraph(esc(text), style))


def add_paragraphs(story: list, styles, paragraphs: list[str]) -> None:
    for text in paragraphs:
        story.append(p(text, styles["Body"]))


def add_bullets(story: list, styles, items: list[str]) -> None:
    for item in items:
        story.append(p(f"- {item}", styles["Body"]))


def add_numbered(story: list, styles, items: list[str]) -> None:
    for idx, item in enumerate(items, 1):
        story.append(p(f"{idx}. {item}", styles["Body"]))


def add_note(story: list, styles, title: str, items: list[str], color: str = "#EEF6FF") -> None:
    body = "<br/>".join(esc(f"- {item}") for item in items)
    box = Table(
        [[rich(f"<b>{esc(title)}</b><br/>{body}", styles["Note"])]],
        colWidths=[DOC_WIDTH],
    )
    box.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, -1), colors.HexColor(color)),
                ("BOX", (0, 0), (-1, -1), 0.5, colors.HexColor("#B6C7DC")),
                ("LEFTPADDING", (0, 0), (-1, -1), 7),
                ("RIGHTPADDING", (0, 0), (-1, -1), 7),
                ("TOPPADDING", (0, 0), (-1, -1), 6),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
            ]
        )
    )
    story.append(box)
    story.append(Spacer(1, 6))


def add_table(story: list, styles, rows: list[list[str]], widths: list[float] | None = None) -> None:
    if not rows:
        return
    col_count = len(rows[0])
    if widths is None:
        widths = [DOC_WIDTH / col_count] * col_count
    data = []
    for ridx, row in enumerate(rows):
        style = styles["TableHead"] if ridx == 0 else styles["TableBody"]
        data.append([p(cell, style) for cell in row])
    table = Table(data, colWidths=widths, repeatRows=1)
    table.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#EAF2FF")),
                ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor("#D7DEE8")),
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("LEFTPADDING", (0, 0), (-1, -1), 5),
                ("RIGHTPADDING", (0, 0), (-1, -1), 5),
                ("TOPPADDING", (0, 0), (-1, -1), 4),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
            ]
        )
    )
    story.append(table)
    story.append(Spacer(1, 7))


def add_screenshot(story: list, styles, filename: str, caption: str, max_height: float = 210) -> None:
    path = SCREENSHOT_DIR / filename
    if not path.exists():
        add_note(story, styles, "캡처 누락", [f"{filename} 파일을 찾을 수 없습니다."], color="#FFF7ED")
        return

    with PILImage.open(path) as img:
        width_px, height_px = img.size

    scale = min(DOC_WIDTH / width_px, max_height / height_px)
    flowables = [
        Image(str(path), width=width_px * scale, height=height_px * scale),
        p(caption, styles["Caption"]),
    ]
    story.append(KeepTogether(flowables))


def page_break(story: list) -> None:
    story.append(PageBreak())


def build_story(styles):
    story: list = []

    # Cover
    story.append(Spacer(1, 18 * mm))
    story.append(p("거래플랜 사용자 메뉴얼", styles["Title"]))
    story.append(p("사용자 업무 흐름 + 처음 유지보수자를 위한 운영/점검 가이드", styles["Subtitle"]))
    add_screenshot(
        story,
        styles,
        "03_main.png",
        f"대표 화면 예시: {CAPTURE_DATE} 캡처 당시 Desktop {CAPTURE_DESKTOP_VERSION}의 메인 대시보드",
        max_height=175,
    )
    add_table(
        story,
        styles,
        [
            ["항목", "내용"],
            ["문서명", "거래플랜 사용자 메뉴얼"],
            ["기능 문서 기준", DOC_DATE],
            [
                "로컬 Desktop 소스",
                f"{LOCAL_DESKTOP_VERSION} / FileVersion {LOCAL_DESKTOP_FILE_VERSION}",
            ],
            ["공개 stable Desktop", PUBLIC_STABLE_DESKTOP_VERSION],
            [
                "Android 현재 소스",
                f"{ANDROID_VERSION} / versionCode {ANDROID_VERSION_CODE}",
            ],
            [
                "Android 공개 stable",
                f"{PUBLIC_STABLE_ANDROID_VERSION} / {PUBLIC_STABLE_ANDROID_FILENAME}",
            ],
            ["주요 대상", "일반 사용자, 운영 관리자, 처음 유지보수하는 담당자"],
            ["화면 캡처 기준", f"{CAPTURE_DATE} / Desktop {CAPTURE_DESKTOP_VERSION} / current Release exact WPF"],
        ],
        widths=[95, DOC_WIDTH - 95],
    )
    add_note(
        story,
        styles,
        "문서 사용 전 주의",
        [
            f"화면 캡처는 {CAPTURE_DATE}의 Desktop {CAPTURE_DESKTOP_VERSION} current Release에서 실제 WPF 36개 창을 768회 측정한 exact 결과 중 선별한 15개 화면입니다.",
            f"exact 결과 SHA-256은 {CAPTURE_RESULT_SHA256}이며 모델링 측정은 {CAPTURE_MODELLED_MEASUREMENT_COUNT}건입니다. 화면은 합성·익명 상태이고 운영 데이터 저장 동작은 수행하지 않았습니다.",
            f"기능 설명은 {DOC_DATE}의 로컬 Desktop 소스 {LOCAL_DESKTOP_VERSION}/FileVersion {LOCAL_DESKTOP_FILE_VERSION}, 공개 stable Desktop {PUBLIC_STABLE_DESKTOP_VERSION}, Android 현재 소스 {ANDROID_VERSION}/versionCode {ANDROID_VERSION_CODE}, Android 공개 stable {PUBLIC_STABLE_ANDROID_VERSION}를 분리해 기록했습니다.",
            "공개 stable의 실제 live manifest·다운로드, Android 서명 연속성·실기기 업데이트, 실물 프린터 출력은 이 문서 생성 과정에서 검증하지 않았습니다.",
            "삭제, 수금, 청구, 전표 저장은 연결 데이터가 있으므로 운영 DB에서 임의 테스트하지 마세요.",
            "유지보수자는 화면 오류를 보기 전에 권한, 담당지점, 동기화 상태, 서버 연결 상태를 먼저 확인하세요.",
        ],
    )
    page_break(story)

    # TOC
    story.append(Paragraph("목차", styles["TocTitle"]))
    toc = TableOfContents()
    toc.levelStyles = [
        ParagraphStyle(
            name="TOCLevel1",
            fontName=FONT_BOLD,
            fontSize=9.6,
            leading=13,
            leftIndent=0,
            firstLineIndent=0,
            spaceBefore=4,
            textColor=colors.HexColor("#0F172A"),
        ),
        ParagraphStyle(
            name="TOCLevel2",
            fontName=FONT_REGULAR,
            fontSize=8.6,
            leading=11.5,
            leftIndent=14,
            firstLineIndent=0,
            textColor=colors.HexColor("#475569"),
        ),
    ]
    story.append(toc)
    page_break(story)

    add_heading(story, styles, 1, "1. 문서 사용 방법")
    add_paragraphs(
        story,
        styles,
        [
            "이 문서는 거래플랜을 처음 사용하는 사람과 처음 유지보수를 맡는 사람이 같은 화면을 보고 같은 기준으로 대화할 수 있도록 제작했습니다. 사용자는 업무 순서를 따라가고, 유지보수자는 각 화면의 데이터 영향과 점검 순서를 확인하면 됩니다.",
            "일반 사용자는 2장부터 13장까지의 화면 설명을 먼저 읽으세요. 유지보수자는 14장 이후의 권한, 동기화, 개발/운영 점검, 문제 해결표를 함께 확인해야 합니다.",
        ],
    )
    add_table(
        story,
        styles,
        [
            ["읽는 사람", "먼저 볼 장", "중점"],
            ["일반 사용자", "2장 빠른 시작, 4장 로그인, 5장 메인화면", "업무 흐름, 메뉴 위치, 저장/인쇄 방법"],
            ["회계/정산 담당자", "9장 수금/지급, 10장 장부/집계, 12장 렌탈 청구", "결제 연결, 기간 집계, 청구 삭제 영향"],
            ["렌탈 담당자", "11장 신규 렌탈, 12장 청구관리, 13장 자산/대시보드", "청구 프로필, 자산 상태, 설치처/청구처 차이"],
            ["Android 사용자", "17장 Android 모바일 앱", "지원 조회·입력 범위와 안전 업데이트 조건"],
            ["유지보수자", "14장 이후 전체", "권한/범위, 동기화, 로컬/서버 점검, live 반영 안전"],
        ],
        widths=[95, 155, DOC_WIDTH - 250],
    )
    add_note(
        story,
        styles,
        "유지보수자가 기억할 원칙",
        [
            "자산 조회 범위, 품목 범위, 청구/전표 범위, 테넌트/업체 범위는 서로 같다고 가정하지 않습니다.",
            "전표, 청구, 수금/지급, 렌탈 자산은 서로 연결될 수 있으므로 단일 화면만 보고 삭제 여부를 판단하지 않습니다.",
            "저장 성공과 서버 동기화 성공은 다릅니다. 로컬 반영, dirty 데이터, 서버 반영 여부를 분리해서 확인합니다.",
            "live 반영 전후에는 거래플랜 Linux PC 상태를 우선 확인하고, 공통 인프라 영향 가능성이 있으면 워크플랜, itw 홈페이지 접속 상태도 확인합니다.",
        ],
    )

    add_heading(story, styles, 1, "2. 거래플랜 전체 업무 지도")
    add_heading(story, styles, 2, "2.1 일반 매출/매입 흐름")
    add_numbered(
        story,
        styles,
        [
            "로그인 후 메인화면에서 서버 연결과 사용자 권한을 확인합니다.",
            "거래처 관리에서 거래처를 등록하거나 기존 거래처 정보를 확인합니다.",
            "품목/재고 관리에서 품목, 단가, 재고방식, 운영유형을 확인합니다.",
            "판매작성, 구매작성, 견적작성, 발주작성 화면에서 전표를 작성합니다.",
            "항목추가로 라인을 입력하고 수량, 단가, 공급가, 부가세, 합계를 검토합니다.",
            "수금 입력 또는 지급 입력으로 결제 내역을 등록합니다.",
            "매입/매출 장부와 기간별 집계에서 기간별 자료를 확인하고 엑셀 또는 인쇄 결과를 검토합니다.",
        ],
    )
    add_heading(story, styles, 2, "2.2 렌탈 흐름")
    add_numbered(
        story,
        styles,
        [
            "신규 렌탈 등록에서 거래처정보, 렌탈 기본정보, 임대료 청구 설정, 장비 연결, 청구항목 구성을 순서대로 입력합니다.",
            "렌탈 자산/설치현황에서 관리번호, 기계번호, 현재 상태, 설치처, 청구 거래처를 확인합니다.",
            "렌탈 청구관리에서 기준일, 상태, 거래처 필터를 맞추고 청구 대상을 조회합니다.",
            "청구시작으로 청구 전표를 만들고, 수금등록으로 입금 내역을 연결합니다.",
            "청구/입금 내역을 삭제할 때는 연결된 판매전표와 입금 내역까지 함께 삭제되는지 확인합니다.",
        ],
    )
    add_table(
        story,
        styles,
        [
            ["기준 데이터", "연결되는 화면", "수정/삭제 시 영향"],
            ["거래처", "전표, 수금/지급, 계약서, 렌탈 청구, 렌탈 자산", "거래처명/거래구분/담당지점 변경은 조회 결과와 인쇄물에 영향"],
            ["품목", "판매/구매/견적/발주 전표, 재고관리", "단가, 과세구분, 재고방식 변경은 신규 전표 입력 기준에 영향"],
            ["전표", "수금/지급, 인쇄, 장부, 렌탈 청구 연동", "전표 삭제/수정은 결제 연결과 집계 결과에 영향"],
            ["렌탈 청구", "판매전표, 입금 내역, 렌탈 프로필", "청구/입금 삭제는 연결 판매전표/입금 데이터 동시 삭제 가능"],
            ["렌탈 자산", "렌탈 등록, 설치현황, 청구 거래처", "현재 거래처와 청구 거래처가 다를 수 있어 범위 확인 필수"],
        ],
        widths=[88, 150, DOC_WIDTH - 238],
    )

    add_heading(story, styles, 1, "3. 설치, 실행, 업데이트 기본")
    add_heading(story, styles, 2, "3.1 사용자 PC에서 확인할 것")
    add_bullets(
        story,
        styles,
        [
            "바탕화면 또는 시작 메뉴에서 거래플랜 실행 아이콘을 사용합니다.",
            "로그인 화면이 열리면 서버 주소, 네트워크 연결, 계정/비밀번호를 확인합니다.",
            f"환경설정 또는 버전/업데이트 영역에서 현재 버전을 확인합니다. 문서 작성 시점의 로컬 Desktop 소스와 화면 캡처는 {LOCAL_DESKTOP_VERSION}, 공개 stable Desktop은 {PUBLIC_STABLE_DESKTOP_VERSION}입니다.",
            "서버가 열리지 않는다는 메시지는 프로그램 실행 문제와 서버/API 연결 문제를 구분해서 봐야 합니다.",
        ],
    )
    add_heading(story, styles, 2, "3.2 유지보수자가 알아야 할 프로젝트 위치")
    add_table(
        story,
        styles,
        [
            ["영역", "대표 경로", "설명"],
            ["데스크톱 앱", r"D:\거래플랜\Desktop\거래플랜.Desktop.App", "WPF 데스크톱 클라이언트"],
            ["서버 API", r"D:\거래플랜\Server\거래플랜.Server.Api", "로그인, 동기화, 운영 API"],
            ["공유 계약", r"D:\거래플랜\Shared\거래플랜.Shared.Contracts", "클라이언트/서버 공통 DTO와 계약"],
            ["테스트", r"D:\거래플랜\Tests", "Desktop/API 테스트 프로젝트"],
            ["업데이트 내역", r"D:\거래플랜\업데이트 내역.md", "작업 변경 기록. 반드시 append 방식으로 기록"],
        ],
        widths=[80, 190, DOC_WIDTH - 270],
    )
    add_heading(story, styles, 2, "3.3 기본 빌드/점검 명령")
    add_table(
        story,
        styles,
        [
            ["목적", "명령"],
            ["전체 빌드", r"D:\거래플랜\.dotnet\dotnet.exe build D:\거래플랜\거래플랜.sln -c Debug"],
            ["데스크톱 테스트", r"D:\거래플랜\.dotnet\dotnet.exe test D:\거래플랜\Tests\GeoraePlan.Desktop.App.Tests\GeoraePlan.Desktop.App.Tests.csproj"],
            ["API 테스트", r"D:\거래플랜\.dotnet\dotnet.exe test D:\거래플랜\Tests\GeoraePlan.Server.Api.Tests\GeoraePlan.Server.Api.Tests.csproj"],
        ],
        widths=[90, DOC_WIDTH - 90],
    )
    add_note(
        story,
        styles,
        "주의",
        [
            "사용자 PC 장애 대응 중에는 운영 DB에 직접 수정 쿼리를 실행하지 않는 것을 원칙으로 합니다.",
            "수정이 필요한 경우 먼저 재현, 백업, 영향 범위, 복구 방법을 정리한 뒤 진행합니다.",
        ],
        color="#FFF7ED",
    )

    add_heading(story, styles, 1, "4. 로그인과 서버 연결")
    add_screenshot(story, styles, "01_login.png", "로그인 화면: 계정/비밀번호 입력, 자동 로그인, 오프라인 모드 확인", max_height=230)
    add_heading(story, styles, 2, "4.1 로그인 절차")
    add_numbered(
        story,
        styles,
        [
            "아이디와 비밀번호를 입력합니다.",
            "자동 로그인이 필요한 PC에서는 자동 로그인 체크 여부를 확인합니다. 공용 PC에서는 권장하지 않습니다.",
            "로그인 버튼을 누른 뒤 서버 연결 오류, 계정 오류, 권한 오류 문구를 구분합니다.",
            "오프라인 모드는 최근 정상 로그인 캐시가 있는 경우에만 제한적으로 사용할 수 있습니다.",
        ],
    )
    add_heading(story, styles, 2, "4.2 서버가 안 열렸다는 메시지가 나올 때")
    add_table(
        story,
        styles,
        [
            ["확인 순서", "확인 내용", "판단 기준"],
            ["1", "인터넷/사내망 연결", "다른 웹사이트와 trade.2884.kr 접속 가능 여부"],
            ["2", "API 상태", "https://trade.2884.kr/healthz 응답 여부"],
            ["3", "앱 설정", "appsettings.json의 BaseUrl이 운영 서버 또는 테스트 서버를 가리키는지"],
            ["4", "계정 권한", "같은 PC에서 다른 계정은 되는지, 특정 계정만 실패하는지"],
            ["5", "운영 인프라", "리버스 프록시, 인증서, Docker API 컨테이너, DB 연결 상태"],
        ],
        widths=[55, 150, DOC_WIDTH - 205],
    )
    add_note(
        story,
        styles,
        "사용자에게 안내할 말",
        [
            "서버 연결 오류는 비밀번호 오류와 다릅니다. 비밀번호를 반복 변경하기 전에 서버 상태를 먼저 확인하세요.",
            "같은 시간대 여러 사용자가 동시에 실패하면 PC 문제가 아니라 서버/API 또는 네트워크 문제일 가능성이 큽니다.",
        ],
    )
    page_break(story)

    add_heading(story, styles, 1, "5. 메인화면 사용법")
    add_screenshot(story, styles, "03_main.png", "메인화면: 상단 업무 메뉴, 전표 목록, 선택 전표 라인 미리보기", max_height=230)
    add_heading(story, styles, 2, "5.1 화면 목적")
    add_bullets(
        story,
        styles,
        [
            "상단 메뉴에서 품목/재고, 신규 렌탈, 거래처, 장부, 렌탈 업무, 환경설정으로 이동합니다.",
            "가운데 전표 목록은 최근 전표와 검색 결과를 보여줍니다.",
            "전표를 선택하면 하단 또는 우측의 라인 미리보기에서 품목 라인, 금액, 메모를 빠르게 확인합니다.",
            "전표를 더블클릭하면 실제 전표 상세 화면이 열립니다.",
        ],
    )
    add_heading(story, styles, 2, "5.2 선택 전표 라인 미리보기")
    add_note(
        story,
        styles,
        "중요 동작",
        [
            "선택 전표 라인 미리보기는 실제 전표를 더블클릭해 열었을 때의 라인 순서와 같은 기준으로 표시됩니다.",
            "유지보수 시 미리보기 순서가 이상하면 실제 전표 상세의 라인 순서, 정렬 키, 저장 순서, 화면 바인딩 순서를 같이 확인하세요.",
            "미리보기만 고치고 실제 전표 정렬과 다르게 만들면 사용자 혼선이 생깁니다. 실제 전표를 기준으로 통일합니다.",
        ],
    )
    story.append(CondPageBreak(245))
    add_heading(story, styles, 2, "5.3 상단 메뉴 구성")
    add_screenshot(story, styles, "03_main.png", "메인 상단 업무 메뉴: 최신 메인화면에서 전체 업무 진입점을 확인", max_height=205)
    add_table(
        story,
        styles,
        [
            ["상단 메뉴", "주요 용도", "유지보수 확인 포인트"],
            ["품목/재고 관리", "품목 등록, 가격, 재고방식, 창고별 수량 확인", "품목 권한, 담당지점/창고 범위, 재고방식"],
            ["신규 렌탈 등록", "렌탈 거래처와 청구 설정을 단계별 등록", "드래프트 자동저장, 장비 연결, 청구처/설치처"],
            ["거래처 관리", "거래처 등록/수정/삭제와 계약서 PDF 관리", "거래구분, 고객분류, 담당지점, 계약서 연결"],
            ["매입/매출 장부", "납품/매입/매출 내역 조회", "조회 범위 권한, 기간, 창고 필터"],
            ["기간별 집계", "거래원장/집계 자료 생성", "기간, 집계 종류, 저장 경로"],
            ["렌탈 업무", "렌탈 대시보드, 청구관리, 자산/설치현황", "청구/전표/수금 연동, 자산 상태"],
            ["환경설정/휴지통", "회사정보, 사용자, 권한, 동기화, 백업, 삭제 복원", "관리자 권한, 복원/영구삭제 차단 사유"],
        ],
        widths=[90, 180, DOC_WIDTH - 270],
    )

    story.append(CondPageBreak(270))
    add_heading(story, styles, 1, "6. 거래처 관리")
    add_screenshot(story, styles, "05_customer_management.png", "거래처 관리 화면: 검색, 목록, 신규/수정/삭제 버튼", max_height=225)
    add_heading(story, styles, 2, "6.1 화면 목적")
    add_bullets(
        story,
        styles,
        [
            "거래처는 전표, 수금/지급, 렌탈, 계약서, 장부 조회의 기준 데이터입니다.",
            "목록에서 거래처명, 대표자, 연락처, 사업자번호, 거래구분, 담당지점 등을 확인합니다.",
            "검색어와 거래구분 필터를 함께 사용하면 거래처를 빠르게 찾을 수 있습니다.",
        ],
    )
    add_heading(story, styles, 2, "6.2 신규 등록/수정")
    add_screenshot(story, styles, "06_customer_edit.png", "거래처 등록/수정 화면: 기본정보, 거래처 구분, 담당지점, 계약서 관리", max_height=230)
    add_table(
        story,
        styles,
        [
            ["항목", "설명", "주의"],
            ["거래처명", "전표와 장부에 표시되는 기본 이름", "중복 거래처가 생기지 않도록 검색 후 등록"],
            ["사업자번호", "세금계산서/거래명세서 기준 정보", "형식과 중복 여부 확인"],
            ["거래구분", "매출처, 매입처, 렌탈처 등 업무 구분", "조회 필터와 전표 입력 후보에 영향"],
            ["고객분류/가격등급", "가격 정책과 분류 검색에 사용", "임의 변경 시 단가 적용 혼선 가능"],
            ["담당지점", "조회/저장 권한 범위의 기준", "일반 사용자가 보이지 않는 원인이 될 수 있음"],
            ["계약서 PDF", "거래처에 계약서 파일을 연결", "파일 교체/삭제 시 이전 계약서 필요 여부 확인"],
        ],
        widths=[85, 170, DOC_WIDTH - 255],
    )
    add_note(
        story,
        styles,
        "유지보수 점검",
        [
            "거래처가 목록에 안 보인다는 문의는 삭제 여부보다 먼저 담당지점, 거래구분, 고객분류, 검색어를 확인합니다.",
            "거래처 삭제가 안 되면 전표, 렌탈 프로필, 렌탈 자산, 계약서 연결 여부를 확인합니다.",
        ],
    )

    add_heading(story, styles, 1, "7. 품목/재고 관리")
    add_screenshot(story, styles, "07_inventory.png", "품목/재고 관리 화면: 품목 목록, 재고수량, 창고/분류 필터", max_height=230)
    add_heading(story, styles, 2, "7.1 화면 목적")
    add_bullets(
        story,
        styles,
        [
            "품목은 전표 라인에 입력되는 제품, 장비, 서비스, 비재고 청구항목의 기준입니다.",
            "재고관리 대상 품목은 입출고와 창고별 수량에 영향을 줍니다.",
            "비재고 품목은 렌탈 청구항목이나 서비스 비용처럼 수량 재고를 추적하지 않는 항목에 사용합니다.",
        ],
    )
    add_heading(story, styles, 2, "7.2 품목 확인 포인트")
    add_table(
        story,
        styles,
        [
            ["항목", "사용 목적", "유지보수 주의"],
            ["품목명/규격", "전표 라인과 인쇄물에 표시", "이름 변경은 과거 전표 표시 정책 확인 필요"],
            ["단가/과세구분", "전표 금액 계산 기준", "부가세 포함/별도 계산 로직과 함께 확인"],
            ["재고방식", "재고 추적 여부 결정", "재고 품목을 비재고로 바꾸면 수량 관리 영향"],
            ["창고/담당지점", "조회 범위와 재고 위치", "사용자별 권한 차이 확인"],
            ["운영유형", "판매, 렌탈, 서비스 등 분류", "렌탈 청구 항목과 연계 가능"],
        ],
        widths=[85, 170, DOC_WIDTH - 255],
    )
    add_note(
        story,
        styles,
        "자주 발생하는 문의",
        [
            "전표 작성 중 품목이 검색되지 않으면 품목 상태, 담당지점/창고 범위, 거래 유형에 맞는 품목인지 확인하세요.",
            "재고수량이 맞지 않으면 전표 저장/삭제 이력, 창고 이동, 동기화 지연을 함께 확인하세요.",
        ],
    )

    story.append(CondPageBreak(275))
    add_heading(story, styles, 1, "8. 판매/구매/견적/발주 전표")
    add_screenshot(story, styles, "08_sales_invoice.png", "판매 전표 화면: 거래처, 품목 라인, 금액, 인쇄/저장 버튼", max_height=230)
    add_heading(story, styles, 2, "8.1 전표 공통 구조")
    add_table(
        story,
        styles,
        [
            ["구역", "설명", "확인 포인트"],
            ["상단 기본정보", "전표일자, 거래처, 담당자, 창고, 메모", "날짜와 거래처 선택이 장부/집계 기준"],
            ["품목 라인", "품목, 규격, 수량, 단가, 공급가, 부가세, 합계", "라인 순서와 금액 계산 확인"],
            ["하단 합계", "공급가/부가세/합계/결제 상태", "인쇄 전 합계 재확인"],
            ["우측/하단 기능", "저장, 인쇄, 항목추가, 삭제, 수금/지급 연결", "버튼 권한과 단축키 확인"],
        ],
        widths=[95, 190, DOC_WIDTH - 285],
    )
    add_heading(story, styles, 2, "8.2 작성 순서")
    add_numbered(
        story,
        styles,
        [
            "전표 유형을 확인합니다. 판매, 구매, 견적, 발주는 데이터 영향이 다릅니다.",
            "거래처를 선택하고 전표일자, 담당자, 창고를 입력합니다.",
            "항목추가로 품목 라인을 추가합니다.",
            "수량, 단가, 공급가, 부가세, 합계를 확인합니다.",
            "필요하면 메모와 비고를 입력합니다.",
            "저장 후 메인화면 목록과 라인 미리보기에서 동일한 순서로 표시되는지 확인합니다.",
            "인쇄 전 미리보기에서 거래처 정보, 라인 순서, 금액, 회사 정보를 확인합니다.",
        ],
    )
    add_note(
        story,
        styles,
        "라인 순서 기준",
        [
            "사용자가 보는 최종 기준은 실제 전표 상세 화면입니다.",
            "메인화면 선택 전표 라인 미리보기, 인쇄 미리보기, 저장된 전표 상세는 같은 순서로 보여야 합니다.",
            "순서가 어긋나는 문제를 수정할 때는 화면별 정렬 조건이 아니라 저장된 라인의 일관된 표시 기준을 먼저 확인하세요.",
        ],
    )
    add_heading(story, styles, 2, "8.3 인쇄와 미리보기")
    add_screenshot(story, styles, "18_trade_print.png", "거래플랜 인쇄 화면: 전체 프린터 목록, 상태, 매수, 페이지 범위, PDF/XPS 저장", max_height=220)
    add_bullets(
        story,
        styles,
        [
            "거래명세서와 세금계산서 인쇄는 목적이 다릅니다. 출력 전 양식을 확인하세요.",
            "전표 인쇄[F9] 또는 인쇄하기[F9]를 사용하기 전 거래처 정보와 합계를 확인합니다.",
            "인쇄 결과가 이상하면 회사 설정, 거래처 사업자 정보, 품목 라인 순서, 공급가/부가세 계산을 점검합니다.",
        ],
    )

    add_heading(story, styles, 1, "9. 수금/지급 관리")
    add_screenshot(story, styles, "09_payment.png", "수금/지급 입력 화면: 결제일, 결제수단, 금액, 연결 전표", max_height=230)
    add_heading(story, styles, 2, "9.1 화면 목적")
    add_bullets(
        story,
        styles,
        [
            "수금은 판매대금 회수 내역, 지급은 구매대금 지급 내역을 기록합니다.",
            "전표와 연결하면 미수/미지급 관리와 장부 집계가 정확해집니다.",
            "결제수단, 결제일, 금액, 거래처, 연결 전표를 함께 확인해야 합니다.",
        ],
    )
    add_table(
        story,
        styles,
        [
            ["항목", "설명", "주의"],
            ["거래처", "결제 주체", "전표 거래처와 다른 경우 사유 확인"],
            ["결제일", "장부와 집계 기준일", "실제 입금일과 입력일 구분"],
            ["결제수단", "현금, 계좌, 카드 등", "회계 처리 기준에 맞게 선택"],
            ["금액", "수금/지급 금액", "전표 합계와 부분결제 여부 확인"],
            ["연결 전표", "결제와 전표를 연결", "렌탈 청구에서 생성된 입금은 연결 삭제 영향 확인"],
        ],
        widths=[80, 170, DOC_WIDTH - 250],
    )
    add_note(
        story,
        styles,
        "삭제 전 확인",
        [
            "결제 내역을 삭제하면 전표의 결제 상태와 장부 집계가 바뀔 수 있습니다.",
            "렌탈 청구/입금 내역에서 삭제한 경우 연결된 판매전표와 입금 내역이 함께 삭제될 수 있으므로 화면에 표시된 거래처와 금액을 반드시 확인하세요.",
        ],
        color="#FFF7ED",
    )

    story.append(CondPageBreak(275))
    add_heading(story, styles, 1, "10. 매입/매출 장부와 기간별 집계")
    add_screenshot(story, styles, "10_period_ledger.png", "기간별 집계 화면: 조회 기간, 집계 조건, 결과 목록", max_height=230)
    add_heading(story, styles, 2, "10.1 조회 순서")
    add_numbered(
        story,
        styles,
        [
            "조회 기간을 선택합니다.",
            "거래처, 담당지점, 전표 유형, 상태 필터를 필요에 맞게 조정합니다.",
            "조회 버튼을 눌러 결과를 확인합니다.",
            "결과 건수와 합계가 예상과 다르면 필터를 먼저 확인합니다.",
            "엑셀 저장 또는 인쇄 전에는 기간과 집계 종류를 다시 확인합니다.",
        ],
    )
    add_heading(story, styles, 2, "10.2 장부/집계가 다를 때")
    add_table(
        story,
        styles,
        [
            ["증상", "먼저 확인할 것", "추가 점검"],
            ["거래처가 누락됨", "거래처 필터, 담당지점 권한", "삭제/휴지통 이동 여부"],
            ["금액 합계가 다름", "기간, 전표 유형, 부가세 포함 기준", "부분 수금/지급 연결"],
            ["렌탈 청구가 안 보임", "청구 기준일, 청구 상태, 연결 판매전표", "렌탈 프로필/자산 상태"],
            ["엑셀 저장 실패", "저장 경로 권한, 파일 열림 상태", "백신/보안 프로그램 차단"],
        ],
        widths=[110, 170, DOC_WIDTH - 280],
    )

    story.append(CondPageBreak(275))
    add_heading(story, styles, 1, "11. 신규 렌탈 등록")
    add_screenshot(story, styles, "14_rental_onboarding.png", "신규 렌탈 등록 화면: 거래처, 기본정보, 청구 설정, 장비 연결 단계", max_height=230)
    add_heading(story, styles, 2, "11.1 입력 단계")
    add_numbered(
        story,
        styles,
        [
            "거래처정보에서 고객 또는 청구 거래처를 선택합니다.",
            "렌탈 기본정보에서 계약 시작일, 설치처, 담당자, 상태를 입력합니다.",
            "임대료 청구 설정에서 청구 주기, 청구일, 금액, 부가세 기준을 입력합니다.",
            "장비 연결에서 관리번호, 기계번호, 설치 자산을 연결합니다.",
            "청구항목 구성에서 기본 임대료, 추가 비용, 비재고 청구항목을 확인합니다.",
            "저장 전 청구처와 설치처가 다를 수 있음을 확인합니다.",
        ],
    )
    add_note(
        story,
        styles,
        "유지보수 포인트",
        [
            "렌탈 등록은 드래프트 자동저장, 거래처 검색, 자산 연결, 청구항목 생성이 함께 작동합니다.",
            "한 단계의 오류처럼 보여도 실제 원인은 거래처 범위, 자산 상태, 품목 기준값, 권한 저장 문제일 수 있습니다.",
        ],
    )
    page_break(story)

    add_heading(story, styles, 1, "12. 렌탈 청구관리")
    add_screenshot(story, styles, "12_rental_billing.png", "렌탈 청구관리 화면: 청구 대상 조회, 청구 시작, 수금 등록, 청구/입금 내역", max_height=230)
    add_heading(story, styles, 2, "12.1 청구 처리 순서")
    add_numbered(
        story,
        styles,
        [
            "기준일과 조회 조건을 선택합니다.",
            "청구 대상 목록에서 거래처, 청구 주기, 금액, 상태를 확인합니다.",
            "대상 거래처와 금액이 맞으면 청구시작을 실행합니다.",
            "청구 결과로 생성되거나 연결되는 판매전표를 확인합니다.",
            "입금이 들어오면 수금등록으로 입금 내역을 연결합니다.",
            "청구/입금 내역에서 생성 결과, 연결 전표, 입금 금액을 확인합니다.",
        ],
    )
    add_heading(story, styles, 2, "12.2 청구/입금 내역 삭제")
    add_note(
        story,
        styles,
        "삭제 동작",
        [
            "청구/입금 내역은 내역을 우클릭해 삭제할 수 있습니다.",
            "청구 내역이 해당 거래처의 판매전표와 연결되어 있으면 삭제 시 연결 판매전표도 함께 삭제됩니다.",
            "입금 내역이 수금 데이터와 연결되어 있으면 삭제 시 연결 입금 내역도 함께 삭제됩니다.",
            "삭제 전에는 거래처, 청구월, 금액, 전표번호, 입금일을 반드시 확인하세요.",
        ],
        color="#FFF7ED",
    )
    add_table(
        story,
        styles,
        [
            ["삭제 대상", "같이 확인할 데이터", "삭제 후 확인"],
            ["청구 내역", "연결 판매전표, 전표 라인, 장부 반영", "판매전표 목록과 장부에서 제거 여부"],
            ["입금 내역", "연결 수금 내역, 결제수단, 입금일", "수금/지급 화면과 미수 상태 변경"],
            ["렌탈 프로필", "청구 주기, 청구항목, 자산 연결", "다음 청구 대상 생성 여부"],
        ],
        widths=[90, 190, DOC_WIDTH - 280],
    )
    add_heading(story, styles, 2, "12.3 문제가 생겼을 때")
    add_bullets(
        story,
        styles,
        [
            "청구 대상이 안 나오면 기준일, 청구 주기, 렌탈 상태, 거래처 권한을 확인합니다.",
            "청구 금액이 다르면 청구항목 구성, 부가세 기준, 할인/추가 비용을 확인합니다.",
            "삭제 후 전표가 남아 있으면 연결 키, 동기화 상태, 휴지통 이동 여부를 확인합니다.",
        ],
    )

    add_heading(story, styles, 1, "13. 렌탈 자산/설치현황과 대시보드")
    add_screenshot(story, styles, "13_rental_assets.png", "렌탈 자산/설치현황 화면: 자산 상태, 설치처, 청구 거래처, 관리번호", max_height=220)
    add_heading(story, styles, 2, "13.1 자산/설치현황 확인")
    add_table(
        story,
        styles,
        [
            ["항목", "설명", "주의"],
            ["관리번호/기계번호", "실물 장비 식별 기준", "중복 또는 누락 여부 확인"],
            ["현재 상태", "재고, 설치, 회수, 점검 등", "청구 가능 여부와 연결"],
            ["현재 거래처", "장비가 설치된 거래처", "청구 거래처와 다를 수 있음"],
            ["청구 거래처", "청구서/전표를 받을 거래처", "권한/범위가 다르면 조회 누락 가능"],
            ["설치처", "실제 설치 위치", "주소 변경과 자산 이동 이력 확인"],
        ],
        widths=[95, 165, DOC_WIDTH - 260],
    )
    add_screenshot(story, styles, "17_rental_dashboard.png", "렌탈 대시보드: 렌탈 상태와 알림을 빠르게 확인하는 화면", max_height=210)
    story.append(CondPageBreak(100))
    add_heading(story, styles, 2, "13.2 대시보드 사용")
    add_bullets(
        story,
        styles,
        [
            "청구 예정, 미수, 자산 상태, 알림성 데이터를 빠르게 확인합니다.",
            "대시보드는 요약 화면이므로 이상 건 발견 시 청구관리와 자산/설치현황으로 이동해 원본 데이터를 확인합니다.",
            "요약 수치가 맞지 않으면 필터, 담당지점, 동기화 지연, 삭제/복원 상태를 점검합니다.",
        ],
    )

    story.append(CondPageBreak(270))
    add_heading(story, styles, 1, "14. 환경설정, 권한, 휴지통")
    add_screenshot(story, styles, "15_environment_settings.png", "환경설정 화면: 회사 설정, 기준값, 권한, 동기화, 업데이트", max_height=220)
    add_heading(story, styles, 2, "14.1 환경설정 탭")
    add_table(
        story,
        styles,
        [
            ["탭", "목적", "주의사항"],
            ["회사 설정", "거래명세서/세금계산서에 표시될 회사 정보 관리", "관리자 권한 필요"],
            ["선택값 관리", "고객분류, 가격등급, 거래구분, 품목분류 등 기준값 관리", "기준값 변경은 검색/전표 입력에 영향"],
            ["담당지점 관리", "USENET, ITWORLD, YEONSU 등 운영 지점 기준 확인", "임의 추가/삭제보다 운영 정책 확인 우선"],
            ["업체/데이터 권한", "테넌트, 지점, 읽기/쓰기 범위 관리", "전표/렌탈/품목/자산 범위를 별도로 확인"],
            ["사용자 관리", "계정, 역할, 권한 설정", "관리자 권한 필요"],
            ["동기화", "서버 반영, 동기화 진단, 백업 실행", "저장 성공과 서버 반영 성공을 구분"],
            ["버전/업데이트", "현재 버전과 업데이트 확인", "사용자 PC 버전 확인"],
        ],
        widths=[90, 190, DOC_WIDTH - 280],
    )
    add_screenshot(story, styles, "19_sync_diagnostics.png", "동기화 진단 화면: 서버 연결, 미해결 항목, outbox와 복구 상태 확인", max_height=220)
    add_heading(story, styles, 2, "14.2 삭제와 복원 원칙")
    add_bullets(
        story,
        styles,
        [
            "일반 삭제는 휴지통 이동입니다. 운영 데이터 복구 가능성을 남기기 위한 구조입니다.",
            "영구삭제 전에는 연결 전표, 결제, 렌탈 프로필, 렌탈 자산, 계약서를 확인합니다.",
            "영구삭제가 막히면 삭제 차단 사유를 먼저 확인하고, 필요한 경우 연결 이동 후 삭제 기능을 검토합니다.",
            "휴지통에서 복원하면 원래 화면에서 조회되는지, 권한 범위 안에 들어오는지 확인합니다.",
        ],
    )

    add_heading(story, styles, 1, "15. 권한, 범위, 데이터 연동 이해")
    add_table(
        story,
        styles,
        [
            ["구분", "관리자", "일반 사용자"],
            ["조회 범위", "전체 또는 넓은 담당지점/업체 범위 조회 가능", "배정된 담당지점/업체 범위 중심 조회"],
            ["저장/삭제", "거래처, 품목, 전표, 렌탈, 환경설정 저장 가능", "일부 저장/삭제 제한 가능"],
            ["환경설정", "회사 설정, 사용자, 권한, 백업 접근 가능", "대부분 조회 제한 또는 수정 제한"],
            ["유지보수 확인", "테넌트/담당지점/권한 설정 오류 가능성 확인", "범위 밖 데이터가 보이지 않는 것이 정상인지 확인"],
        ],
        widths=[88, (DOC_WIDTH - 88) / 2, (DOC_WIDTH - 88) / 2],
    )
    add_heading(story, styles, 2, "15.1 범위를 따로 확인해야 하는 이유")
    add_bullets(
        story,
        styles,
        [
            "자산 조회 범위는 설치처와 현재 거래처 기준일 수 있습니다.",
            "청구/전표 범위는 청구 거래처와 전표 담당지점 기준일 수 있습니다.",
            "품목 범위는 창고, 담당지점, 운영유형 기준일 수 있습니다.",
            "테넌트/업체 범위는 서버 동기화와 권한 저장 가능 여부에 영향을 줍니다.",
        ],
    )
    add_note(
        story,
        styles,
        "범위 관련 변경 전 최소 검증",
        [
            "자산 조회, 품목관리, 청구/전표, 동기화/dirty, 권한 저장 가능 여부를 각각 확인합니다.",
            "한 범위를 넓히는 수정이 다른 화면의 데이터 노출을 넓히지 않는지 확인합니다.",
        ],
    )
    page_break(story)

    add_heading(story, styles, 1, "16. 동기화, 백업, 로컬 데이터")
    add_heading(story, styles, 2, "16.1 동기화 이해")
    add_bullets(
        story,
        styles,
        [
            "거래플랜은 사용자 PC의 로컬 데이터와 서버 데이터를 함께 사용합니다.",
            "사용자는 저장 버튼을 눌렀다고 생각하지만, 유지보수자는 로컬 저장과 서버 반영을 분리해서 봐야 합니다.",
            "dirty 데이터는 로컬에는 저장됐지만 서버 반영이 끝나지 않은 변경을 의미할 수 있습니다.",
            "서버가 일시적으로 끊기면 나중에 동기화될 수 있으므로 중복 저장을 유도하지 않습니다.",
        ],
    )
    add_heading(story, styles, 2, "16.2 백업과 복원 절차")
    add_numbered(
        story,
        styles,
        [
            "삭제/복구, 권한 변경, 대량 수정 전에는 백업 화면에서 `현재 DB 백업`을 실행합니다.",
            "생성된 `.gpbackup`이 백업 목록에 표시되는지 확인하고, 필요한 복원본을 목록에서 선택합니다.",
            "`선택 백업 복원 예약`을 실행합니다. 복원은 즉시 덮어쓰지 않고 다음 앱 실행 때 적용됩니다.",
            "앱을 정상 종료한 뒤 다시 실행하고, 복원 상태와 대상 세대를 확인합니다.",
        ],
    )
    add_bullets(
        story,
        styles,
        [
            "`.gpbackup`은 DB와 첨부 파일을 같은 백업 세대로 묶어 무결성을 확인하는 백업 단위입니다. DB만 또는 첨부만 따로 바꾸지 않습니다.",
            "백업·복원은 Admin 또는 `Data.BackupRestore` 권한이 있는 사용자만 수행합니다.",
            "복원이 실패하면 같은 예약을 반복 적용하지 말고 상태와 오류 기록, 대상 백업의 무결성을 먼저 확인합니다.",
            "문제가 발생한 사용자 PC의 로컬 DB 상태와 운영 서버 상태는 구분해 기록합니다.",
        ],
    )
    add_note(
        story,
        styles,
        "운영 Linux 자동 백업 상태",
        [
            "운영 Linux 자동 백업은 live 설치 승인을 기다리는 상태입니다. 설치·스케줄·외부 복제까지 정상 운영 중이라고 표현하거나 전제하지 않습니다.",
        ],
        color="#FFF7ED",
    )
    add_table(
        story,
        styles,
        [
            ["증상", "로컬 확인", "서버 확인"],
            ["내 PC에는 보이는데 다른 PC에는 안 보임", "dirty 데이터, 로컬 저장 상태", "API 반영/동기화 로그"],
            ["다른 PC에는 보이는데 내 PC에는 안 보임", "필터, 권한, 로컬 동기화 지연", "서버 원본 데이터 존재 여부"],
            ["삭제했는데 다시 나타남", "휴지통/복원, 동기화 충돌", "서버 삭제 반영 여부"],
            ["저장 버튼이 비활성", "권한, 필수값, 화면 상태", "계정 권한/테넌트 범위"],
        ],
        widths=[140, 175, DOC_WIDTH - 315],
    )

    add_heading(story, styles, 1, "17. Android 모바일 앱")
    add_heading(story, styles, 2, "17.1 지원 범위")
    add_table(
        story,
        styles,
        [
            ["구분", "Android 지원 기능"],
            ["접속/홈", "로그인, 홈"],
            ["기준정보", "거래처 조회·입력, 품목 조회·입력"],
            ["전표", "전표 조회, 판매·구매 전표 작성"],
            ["결제", "수금·지급"],
            ["업무 조회", "재고이동 조회, 렌탈 조회, 무결성 상태 조회"],
            ["동기화", "동기화"],
            ["문서/삭제", "계약서 PDF, 휴지통"],
        ],
        widths=[110, DOC_WIDTH - 110],
    )
    add_heading(story, styles, 2, "17.2 PC 전용 또는 Android 미지원")
    add_bullets(
        story,
        styles,
        [
            "사용자·권한 관리",
            "일반 백업/복원",
            "Excel 내보내기와 자료집계",
            "재고이동 생성·수령·반려",
            "렌탈 청구 생성·입금과 렌탈 프로필·자산 수정",
        ],
    )
    add_heading(story, styles, 2, "17.3 Android 안전 업데이트")
    add_bullets(
        story,
        styles,
        [
            "업데이트 APK는 설치된 앱과 같은 서명이어야 하고 versionCode가 반드시 증가해야 합니다.",
            "기존 앱 데이터와 로그인 상태를 보존하는 검증 명령은 정확히 `adb install -r <새 APK 경로>`를 사용합니다.",
            "검증 중 uninstall, 앱 데이터 clear, downgrade를 사용하지 않습니다. 이런 동작은 업데이트 호환성 실패를 숨기거나 사용자 데이터를 지울 수 있습니다.",
            f"공개 stable manifest의 표시 버전은 {PUBLIC_STABLE_ANDROID_VERSION}입니다.",
            f"현재 Android 소스는 {ANDROID_VERSION}/versionCode {ANDROID_VERSION_CODE}입니다. production signing과 게시 연속성을 확인하기 전에는 운영 배포본으로 취급하지 않습니다.",
        ],
    )
    add_note(
        story,
        styles,
        "Android 검증 경계",
        [
            "이 문서는 Android 기능 범위와 안전 조건을 설명합니다. 같은 서명 여부, 실제 versionCode 증가, 실기기 `adb install -r` 성공, 공개 live APK 다운로드는 별도 증거가 없으므로 검증 완료로 주장하지 않습니다.",
        ],
        color="#FFF7ED",
    )
    page_break(story)

    add_heading(story, styles, 1, "18. 첫 유지보수자를 위한 개발/운영 점검")
    add_heading(story, styles, 2, "18.1 재현 우선순위")
    add_numbered(
        story,
        styles,
        [
            "사용자 계정, PC, 앱 버전, 발생 시각, 메뉴, 입력값, 오류 문구를 기록합니다.",
            "같은 계정으로 다른 PC에서 재현되는지 확인합니다.",
            "관리자 계정과 일반 사용자 계정의 결과가 다른지 확인합니다.",
            "필터와 권한 범위를 초기화해도 재현되는지 확인합니다.",
            "로컬 테스트 DB에서 재현한 뒤 코드를 수정합니다.",
        ],
    )
    add_heading(story, styles, 2, "18.2 로컬 실행 점검")
    add_table(
        story,
        styles,
        [
            ["점검", "내용"],
            ["빌드", r"D:\거래플랜\.dotnet\dotnet.exe build D:\거래플랜\거래플랜.sln -c Debug"],
            ["API 프로젝트", r"D:\거래플랜\Server\거래플랜.Server.Api"],
            ["데스크톱 프로젝트", r"D:\거래플랜\Desktop\거래플랜.Desktop.App"],
            ["앱 설정", r"D:\거래플랜\Desktop\거래플랜.Desktop.App\appsettings.json"],
            ["업데이트 기록", r"D:\거래플랜\업데이트 내역.md"],
        ],
        widths=[90, DOC_WIDTH - 90],
    )
    add_heading(story, styles, 2, "18.3 수정 전 질문")
    add_bullets(
        story,
        styles,
        [
            "사용자 입력 오류인지, 권한/범위 문제인지, 코드 버그인지 구분했는가?",
            "전표/청구/수금/자산 연결 데이터가 같이 바뀌는가?",
            "로컬 DB와 서버 DB 둘 다 영향을 받는가?",
            "삭제 또는 복구가 필요한 경우 되돌릴 백업이 있는가?",
            "live 반영 전후 확인 대상과 되돌리기 방법을 정했는가?",
        ],
    )

    add_heading(story, styles, 1, "19. live 운영 안전 체크")
    add_note(
        story,
        styles,
        "Linux PC 운영 안전 규칙",
        [
            "현재 거래플랜 서버 본체는 NAS가 아니라 Linux PC itw@192.168.0.199:2222의 /srv/georaeplan 기준으로 운영합니다.",
            "거래플랜, 워크플랜, itw 홈페이지 작업은 한 번에 하나의 서비스만 진행합니다.",
            "live 반영 전에는 trade.2884.kr 접속 상태와 Linux PC의 거래플랜 API/DB/로그 상태를 확인합니다.",
            "공통 인프라 영향 가능성이 있으면 work.2884.kr, itw.2884.kr 접속 상태도 함께 확인합니다.",
            "Docker 전체 재시작, 전체 prune, 전체 container stop/restart 명령은 사용하지 않습니다.",
            "필요 시 거래플랜 compose project 안에서 api, postgres 같은 명시 서비스만 대상으로 작업합니다.",
            "Linux PC의 Docker daemon, systemd 전체 서비스, nginx/Reverse Proxy 전체 재시작, PostgreSQL 전체 재시작은 사용자 승인 없이 진행하지 않습니다.",
            "live 반영 후에는 trade.2884.kr와 Linux PC 로그에서 502, timeout, Docker daemon, PostgreSQL 연결 오류 여부를 확인합니다.",
            "tools\\nas와 legacy NAS 런북은 과거 호환/참고용이며, 새 운영 작업은 tools\\linux와 Linux PC 기준 절차를 우선합니다.",
        ],
        color="#FFF7ED",
    )
    add_table(
        story,
        styles,
        [
            ["시점", "확인 항목"],
            ["반영 전", "현재 브랜치, 변경 파일, 테스트 결과, 백업, 접속 상태, API/DB 영향"],
            ["반영 중", "거래플랜 compose project 안의 필요한 서비스만 명시 작업"],
            ["반영 후", "로그인, 메인화면, 거래처 조회, 전표 저장, 렌탈 청구 조회, healthz, work/itw 접속"],
            ["문제 발생", "즉시 사용자에게 현상 공유, 롤백 또는 이전 상태 복구 기준 적용"],
        ],
        widths=[90, DOC_WIDTH - 90],
    )

    add_heading(story, styles, 1, "20. 문제 발생 시 확인표")
    add_heading(story, styles, 2, "20.1 로그인 문제")
    add_bullets(
        story,
        styles,
        [
            "아이디/비밀번호가 정확한지 확인합니다.",
            "서버 연결 오류 문구가 있으면 API healthz를 확인합니다.",
            "오프라인 모드는 최근 정상 로그인 캐시가 있어야 사용할 수 있습니다.",
            "로그인 후 화면이 멈춘 것처럼 보이면 대시보드 알림 또는 운영 점검 알림 팝업이 떠 있는지 확인합니다.",
        ],
    )
    add_heading(story, styles, 2, "20.2 조회/저장 문제")
    add_bullets(
        story,
        styles,
        [
            "기간, 유형, 담당지점, 상태 필터가 너무 좁지 않은지 확인합니다.",
            "일반 사용자 계정은 권한 범위 밖 데이터가 안 보일 수 있습니다.",
            "저장 버튼을 누른 뒤 목록 새로고침 또는 화면 재진입으로 반영을 확인합니다.",
            "전표 화면은 자동저장 흐름이 있으므로 닫기 전 안내 메시지를 확인합니다.",
        ],
    )
    add_heading(story, styles, 2, "20.3 삭제 문제")
    add_bullets(
        story,
        styles,
        [
            "거래처/품목/전표/렌탈 데이터 삭제는 휴지통 이동인지 영구삭제인지 구분합니다.",
            "영구삭제가 안 되면 휴지통의 삭제 차단 사유를 확인합니다.",
            "렌탈 청구/입금 내역 삭제는 연결 판매전표와 입금 내역까지 같이 삭제될 수 있으므로 대상 확인이 필수입니다.",
        ],
    )
    add_heading(story, styles, 2, "20.4 인쇄/집계 문제")
    add_bullets(
        story,
        styles,
        [
            "전표 인쇄 전에는 미리보기에서 라인 순서, 공급가, 부가세, 합계를 확인합니다.",
            "세금계산서 인쇄와 거래명세서 인쇄는 용도가 다릅니다.",
            "기간별 집계 결과가 이상하면 기간, 거래처 선택, 집계 종류를 확인합니다.",
            "엑셀 파일 저장 위치는 사용자 문서/Exports 또는 설정된 경로를 확인합니다.",
        ],
    )
    add_table(
        story,
        styles,
        [
            ["문의 문장", "가능한 원인", "첫 대응"],
            ["서버가 안 열렸어요", "API/네트워크/리버스 프록시/인증서 문제", "healthz와 다른 사용자 동시 장애 여부 확인"],
            ["거래처가 사라졌어요", "필터, 권한, 담당지점, 휴지통 이동", "필터 초기화와 휴지통 확인"],
            ["전표 순서가 이상해요", "미리보기/상세/인쇄 정렬 기준 불일치", "실제 전표 상세 순서를 기준으로 확인"],
            ["렌탈 청구를 지웠는데 전표도 사라졌어요", "연결 삭제 정상 동작", "대상 거래처/금액이 맞았는지 로그와 휴지통 확인"],
        ],
        widths=[150, 180, DOC_WIDTH - 330],
    )

    add_heading(story, styles, 1, "21. 작업 체크리스트")
    add_table(
        story,
        styles,
        [
            ["상황", "체크리스트"],
            ["사용자 교육", "로그인, 메인화면, 거래처, 품목, 전표, 수금/지급, 장부, 렌탈 순서로 안내"],
            ["장애 접수", "계정, PC, 버전, 메뉴, 입력값, 오류 문구, 발생 시각, 재현 여부 기록"],
            ["데이터 수정", "백업, 연결 데이터, 권한 범위, 동기화 상태, 복구 방법 확인"],
            ["UI 수정", "기존 디자인 시스템, 버튼 계층, 테이블 밀도, 업무 속도 유지"],
            ["배포", "빌드/테스트, 업데이트 내역, live 전후 접속 상태, 롤백 기준 확인"],
        ],
        widths=[105, DOC_WIDTH - 105],
    )
    add_note(
        story,
        styles,
        "작업 기록",
        [
            "파일을 수정하거나 산출물을 만들면 D:\\거래플랜\\업데이트 내역.md에 append 방식으로 기록합니다.",
            "기록에는 작업 요약, 수정 파일, 주요 변경점, 데이터/로직 영향, 테스트 체크리스트, 남은 주의점을 포함합니다.",
        ],
    )

    add_heading(story, styles, 1, "22. 용어 정리")
    add_table(
        story,
        styles,
        [
            ["용어", "뜻"],
            ["전표", "판매/구매/견적/발주와 품목 라인, 금액, 세금계산, 수금/지급 연결의 기본 문서"],
            ["수금/지급", "판매대금 회수 또는 구매대금 지급 내역"],
            ["거래처", "전표, 렌탈, 계약서, 결제의 기준 고객/업체"],
            ["품목", "전표 라인에 들어가는 제품, 자산, 비재고 청구항목"],
            ["렌탈 청구 프로필", "렌탈 거래처의 청구 주기, 금액, 청구항목, 장비 연결 설정"],
            ["렌탈 자산", "관리번호/기계번호로 식별되는 실제 설치 장비"],
            ["담당지점", "USENET, ITWORLD, YEONSU 등 조회/저장 범위를 나누는 운영 단위"],
            ["동기화", "로컬 DB 변경을 서버에 반영하고 서버 데이터를 로컬로 가져오는 과정"],
            ["휴지통", "삭제된 데이터를 복원하거나 영구삭제하기 전 확인하는 화면"],
        ],
        widths=[115, DOC_WIDTH - 115],
    )

    add_heading(story, styles, 1, "23. 캡처 이미지 목록")
    add_table(
        story,
        styles,
        [
            ["파일명", "설명"],
            ["01_login.png", "로그인 화면"],
            ["03_main.png", "메인 대시보드"],
            ["05_customer_management.png", "거래처 관리 목록"],
            ["06_customer_edit.png", "거래처 등록/수정"],
            ["07_inventory.png", "품목/재고 관리"],
            ["08_sales_invoice.png", "판매 전표"],
            ["09_payment.png", "수금/지급 입력"],
            ["10_period_ledger.png", "기간별 집계"],
            ["12_rental_billing.png", "렌탈 청구관리"],
            ["13_rental_assets.png", "렌탈 자산/설치현황"],
            ["14_rental_onboarding.png", "신규 렌탈 등록"],
            ["15_environment_settings.png", "환경설정"],
            ["17_rental_dashboard.png", "렌탈 대시보드"],
            ["18_trade_print.png", "전용 인쇄창과 프린터 목록"],
            ["19_sync_diagnostics.png", "동기화 진단"],
        ],
        widths=[170, DOC_WIDTH - 170],
    )
    add_paragraphs(
        story,
        styles,
        [
            "마지막 권장 순서: 로그인하고 메인화면 구조를 익힌 뒤 거래처, 품목, 전표, 수금/지급, 장부, 렌탈, 환경설정 순서로 확인하세요. 유지보수자는 같은 순서로 데이터 연결과 권한 범위를 점검하면 원인 파악이 빨라집니다.",
        ],
    )
    return story


def validate_pdf() -> dict:
    if not OUTPUT_PATH.is_file() or not REQUESTED_PATH.is_file():
        raise FileNotFoundError("PDF 출력본과 루트 사본이 모두 있어야 합니다.")

    output_hash = sha256_file(OUTPUT_PATH)
    requested_hash = sha256_file(REQUESTED_PATH)
    if output_hash != requested_hash:
        raise ValueError(
            "PDF 출력본과 루트 사본의 SHA-256이 다릅니다: "
            f"output={output_hash} root={requested_hash}"
        )

    reader = PdfReader(OUTPUT_PATH)
    if reader.is_encrypted:
        raise ValueError("사용자 메뉴얼 PDF는 암호화되지 않아야 합니다.")
    if len(reader.pages) < 20:
        raise ValueError(f"사용자 메뉴얼 페이지 수가 비정상적으로 적습니다: {len(reader.pages)}")
    if (reader.metadata or {}).get("/Title") != "거래플랜 사용자 메뉴얼":
        raise ValueError("사용자 메뉴얼 PDF title metadata가 올바르지 않습니다.")

    page_text_lengths: list[int] = []
    extracted_pages: list[str] = []
    for page_number, page in enumerate(reader.pages, start=1):
        width = float(page.mediabox.width)
        height = float(page.mediabox.height)
        if abs(width - A4[0]) > 1.0 or abs(height - A4[1]) > 1.0:
            raise ValueError(
                f"{page_number}쪽이 A4 크기가 아닙니다: width={width} height={height}"
            )

        page_text = page.extract_text() or ""
        visible_text_length = len("".join(page_text.split()))
        if visible_text_length < 100:
            raise ValueError(
                f"{page_number}쪽의 추출 텍스트가 비정상적으로 적습니다: "
                f"{visible_text_length}자"
            )
        page_text_lengths.append(visible_text_length)
        extracted_pages.append(page_text)

    extracted_text = "\n".join(extracted_pages)
    normalized_text = " ".join(extracted_text.split())
    required_fragments = (
        LOCAL_DESKTOP_VERSION,
        LOCAL_DESKTOP_FILE_VERSION,
        PUBLIC_STABLE_DESKTOP_VERSION,
        ANDROID_VERSION,
        ANDROID_VERSION_CODE,
        PUBLIC_STABLE_ANDROID_VERSION,
        PUBLIC_STABLE_ANDROID_FILENAME,
        ".gpbackup",
        "adb install -r",
    )
    missing_fragments = [
        fragment
        for fragment in required_fragments
        if fragment not in normalized_text
    ]
    if missing_fragments:
        raise ValueError(
            "사용자 메뉴얼 PDF 추출 텍스트에 필수 정보가 없습니다: "
            + ", ".join(missing_fragments)
        )

    android_start = normalized_text.rfind("17. Android 모바일 앱")
    android_end = normalized_text.find(
        "18. 첫 유지보수자를 위한 개발/운영 점검",
        android_start + 1,
    )
    if android_start < 0 or android_end <= android_start:
        raise ValueError("사용자 메뉴얼 PDF에서 Android 지원 범위를 분리하지 못했습니다.")

    android_section = normalized_text[android_start:android_end]
    supported_start = android_section.find("17.1 지원 범위")
    pc_only_start = android_section.find(
        "17.2 PC 전용 또는 Android 미지원",
        supported_start + 1,
    )
    update_start = android_section.find(
        "17.3 Android 안전 업데이트",
        pc_only_start + 1,
    )
    if not (0 <= supported_start < pc_only_start < update_start):
        raise ValueError("사용자 메뉴얼 PDF에서 Android 하위 역할 절을 분리하지 못했습니다.")

    android_supported_section = android_section[supported_start:pc_only_start]
    android_pc_only_section = android_section[pc_only_start:update_start]
    android_update_section = android_section[update_start:]
    required_supported_fragments = (
        "거래처 조회·입력",
        "품목 조회·입력",
        "무결성 상태 조회",
        "동기화",
    )
    required_pc_only_fragments = (
        "사용자·권한 관리",
        "일반 백업/복원",
        "Excel 내보내기와 자료집계",
        "재고이동 생성·수령·반려",
        "렌탈 청구 생성·입금과 렌탈 프로필·자산 수정",
    )
    required_update_fragments = (
        f"공개 stable manifest의 표시 버전은 {PUBLIC_STABLE_ANDROID_VERSION}",
        f"현재 Android 소스는 {ANDROID_VERSION}/versionCode {ANDROID_VERSION_CODE}",
        "adb install -r",
    )
    missing_android_fragments = (
        [
            f"17.1:{fragment}"
            for fragment in required_supported_fragments
            if fragment not in android_supported_section
        ]
        + [
            f"17.2:{fragment}"
            for fragment in required_pc_only_fragments
            if fragment not in android_pc_only_section
        ]
        + [
            f"17.3:{fragment}"
            for fragment in required_update_fragments
            if fragment not in android_update_section
        ]
    )
    if missing_android_fragments:
        raise ValueError(
            "사용자 메뉴얼 PDF Android 절에 역할별 필수 정보가 없습니다: "
            + ", ".join(missing_android_fragments)
        )

    verification = {
        "schemaVersion": 2,
        "documentDate": DOC_DATE,
        "artifact": str(OUTPUT_PATH.relative_to(PROJECT_ROOT)).replace("\\", "/"),
        "rootCopy": str(REQUESTED_PATH.relative_to(PROJECT_ROOT)).replace("\\", "/"),
        "sha256": output_hash,
        "fileSize": OUTPUT_PATH.stat().st_size,
        "pageCount": len(reader.pages),
        "pageTextLengthMin": min(page_text_lengths),
        "encrypted": reader.is_encrypted,
        "title": (reader.metadata or {}).get("/Title"),
        "versions": {
            "desktopSource": LOCAL_DESKTOP_VERSION,
            "desktopFileVersion": LOCAL_DESKTOP_FILE_VERSION,
            "desktopStable": PUBLIC_STABLE_DESKTOP_VERSION,
            "androidSource": ANDROID_VERSION,
            "androidSourceVersionCode": ANDROID_VERSION_CODE,
            "androidStable": PUBLIC_STABLE_ANDROID_VERSION,
            "androidStableFileName": PUBLIC_STABLE_ANDROID_FILENAME,
        },
        "screenshots": list(SCREENSHOT_FILES),
        "captureEvidence": {
            "kind": CAPTURE_EVIDENCE_KIND,
            "resultSha256": CAPTURE_RESULT_SHA256,
            "assemblySha256": CAPTURE_ASSEMBLY_SHA256,
            "measurementCount": CAPTURE_MEASUREMENT_COUNT,
            "successScreenshotCount": CAPTURE_SUCCESS_SCREENSHOT_COUNT,
            "modelledMeasurementCount": CAPTURE_MODELLED_MEASUREMENT_COUNT,
        },
    }
    VERIFICATION_PATH.write_text(
        json.dumps(verification, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return verification


DOC_WIDTH = A4[0] - 36 * mm - 36 * mm


def main() -> None:
    args = parse_args()
    configure(resolve_project_root(args.project_root))
    register_fonts()
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    styles = make_styles()
    doc = ManualDocTemplate(
        str(OUTPUT_PATH),
        pagesize=A4,
        leftMargin=18 * mm,
        rightMargin=18 * mm,
        topMargin=22 * mm,
        bottomMargin=18 * mm,
        title="거래플랜 사용자 메뉴얼",
        author="OpenAI Codex",
    )

    story = build_story(styles)
    doc.multiBuild(story)
    stamp_header_footer(OUTPUT_PATH, doc.leftMargin, doc.rightMargin)
    shutil.copyfile(OUTPUT_PATH, REQUESTED_PATH)
    verification = validate_pdf()
    print(f"generated_bytes={verification['fileSize']}")
    print("root_copy_sha_equal=true")
    print("verified=output/pdf/georaeplan-user-manual.verification.json")
    print(f"pages={verification['pageCount']}")
    print(f"sha256={verification['sha256']}")


if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""렌탈재고관리 원장과 거래플랜 렌탈자산 DB를 읽기 전용으로 대조한다.

이 스크립트는 DB를 절대 수정하지 않는다. PowerShell 래퍼가 Excel .xlsb 시트를
TSV로 추출한 뒤, 이 분석기가 기존 자산 갱신/신규 추가/수동 검토 후보를 분리한다.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Iterable

import pandas as pd


SOURCE_COLUMNS = [
    "관리ID",
    "관리번호",
    "관리업체",
    "현재위치",
    "상품분류",
    "제조사",
    "모델명",
    "기계번호",
    "매입처",
    "매입일",
    "폐기일",
    "매입가",
    "판매가",
    "고객명",
    "설치위치",
    "보증금",
    "렌탈요금",
    "계약기간",
    "계약일",
    "설치일",
    "계약시작",
    "렌탈만료",
    "무상품목",
    "유상품목",
    "K제한",
    "C제한",
    "K추가",
    "C추가",
    "기타사항",
    "회수1",
    "렌탈1",
    "회수2",
    "렌탈2",
    "회수3",
    "렌탈3",
]


ASSET_QUERY = """
SELECT
    a.Id,
    a.ManagementId,
    a.ManagementNumber,
    a.ManagementCompanyCode,
    a.CurrentLocation,
    a.ItemCategoryName,
    a.Manufacturer,
    a.ItemName,
    a.MachineNumber,
    a.PurchaseVendor,
    a.PurchaseDate,
    a.DisposalDate,
    a.PurchasePrice,
    a.SalePrice,
    a.CustomerName,
    a.CurrentCustomerName,
    a.InstallLocation,
    a.InstallSiteName,
    a.DepositText,
    a.MonthlyFee,
    a.ContractMonths,
    a.ContractDate,
    a.InstallDate,
    a.ContractStartDate,
    a.RentalEndDate,
    a.FreeSupplyItems,
    a.PaidSupplyItems,
    a.CustomerId,
    a.BillingProfileId,
    a.ResponsibleOfficeCode,
    a.Revision,
    COALESCE((
        SELECT COUNT(*)
        FROM RentalAssetAssignmentHistories h
        WHERE h.AssetId = a.Id AND COALESCE(h.IsDeleted, 0) = 0
    ), 0) AS AssignmentHistoryCount,
    COALESCE((
        SELECT COUNT(*)
        FROM RentalAssetAssignmentHistories h
        WHERE h.AssetId = a.Id AND COALESCE(h.IsDeleted, 0) = 0 AND COALESCE(h.IsCurrent, 0) = 1
    ), 0) AS CurrentAssignmentHistoryCount
FROM RentalAssets a
WHERE COALESCE(a.IsDeleted, 0) = 0
"""


CUSTOMER_QUERY = """
SELECT Id, NameOriginal, ResponsibleOfficeCode, OfficeCode, TenantCode
FROM Customers
WHERE COALESCE(IsDeleted, 0) = 0
"""


@dataclass(frozen=True)
class SourceAsset:
    excel_row: int
    raw: dict[str, str]

    @property
    def management_id(self) -> str:
        return self.raw.get("관리ID", "").strip()

    @property
    def management_number(self) -> str:
        return self.raw.get("관리번호", "").strip()

    @property
    def management_company_name(self) -> str:
        return self.raw.get("관리업체", "").strip()

    @property
    def management_company_code(self) -> str:
        return normalize_management_company_code(self.management_company_name)

    @property
    def current_location(self) -> str:
        return self.raw.get("현재위치", "").strip()

    @property
    def customer_name(self) -> str:
        return self.raw.get("고객명", "").strip()

    @property
    def machine_number(self) -> str:
        return self.raw.get("기계번호", "").strip()

    @property
    def monthly_fee_number(self) -> int | None:
        return parse_money(self.raw.get("렌탈요금", ""), blank_as_none=True)

    @property
    def purchase_price_number(self) -> int | None:
        return parse_money(self.raw.get("매입가", ""), blank_as_none=True)


def normalize_text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value).replace("\r", " ").replace("\n", " ").strip()


def normalize_key(value: Any) -> str:
    text = normalize_text(value).upper()
    return re.sub(r"\s+", "", text)


def normalize_customer_key(value: Any) -> str:
    text = normalize_text(value)
    text = text.replace("㈜", "주식회사").replace("(주)", "주식회사").replace("（주）", "주식회사")
    text = re.sub(r"[\[\]\(\)（）\{\}<>\s·ㆍ\-_./,]", "", text)
    return text.upper()


def normalize_unknown_serial(value: Any) -> str:
    text = normalize_text(value)
    return "" if text in {"", "미상", "UNKNOWN", "Unknown", "unknown"} else text


def normalize_management_company_code(value: Any) -> str:
    text = normalize_key(value)
    if not text:
        return ""
    if "아이티월드" in text or "ITWORLD" in text:
        return "ITWORLD"
    if "유즈넷" in text or "USENET" in text or "UZNET" in text:
        return "USENET"
    if "연수" in text or "YEONSU" in text:
        return "YEONSU"
    return text


def parse_money(value: Any, *, blank_as_none: bool = False) -> int | None:
    text = normalize_text(value)
    if not text:
        return None if blank_as_none else 0
    if text in {"무료", "무", "면제", "-"}:
        return 0
    numeric = re.sub(r"[^0-9.\-]", "", text)
    if not numeric:
        return None if blank_as_none else 0
    try:
        return int(round(float(numeric), 0))
    except ValueError:
        return None if blank_as_none else 0


def normalize_date_text(value: Any) -> str:
    text = normalize_text(value)
    if not text:
        return ""
    if re.fullmatch(r"\d{4}-\d{1,2}-\d{1,2}", text):
        yyyy, mm, dd = text.split("-")
        return f"{int(yyyy):04d}-{int(mm):02d}-{int(dd):02d}"
    if re.fullmatch(r"\d{4}/\d{1,2}/\d{1,2}", text):
        yyyy, mm, dd = text.split("/")
        return f"{int(yyyy):04d}-{int(mm):02d}-{int(dd):02d}"
    if re.fullmatch(r"\d+(\.0+)?", text):
        try:
            serial = float(text)
            if 25_000 <= serial <= 80_000:
                date = datetime(1899, 12, 30) + timedelta(days=serial)
                return date.strftime("%Y-%m-%d")
        except ValueError:
            pass
    return text


def is_truthy_database_value(value: Any) -> bool:
    text = normalize_text(value)
    return bool(text and text.lower() not in {"none", "null"})


def read_source_assets(source_tsv: Path) -> list[SourceAsset]:
    df = pd.read_csv(source_tsv, sep="\t", dtype=str, keep_default_na=False)
    df = df.loc[~df.apply(lambda row: all(normalize_text(value) == "" for value in row), axis=1)].copy()
    df.columns = [normalize_text(col) for col in df.columns]

    # 헤더가 비어 있거나 중복될 때도 1~35번째 컬럼은 원장 표준 컬럼명으로 보정한다.
    rename_map: dict[str, str] = {}
    for index, expected in enumerate(SOURCE_COLUMNS):
        if index < len(df.columns):
            rename_map[df.columns[index]] = expected
    df = df.rename(columns=rename_map)

    assets: list[SourceAsset] = []
    for row_index, row in df.iterrows():
        raw = {column: normalize_text(row[column]) if column in row else "" for column in SOURCE_COLUMNS}
        if not raw["관리ID"] or not raw["관리번호"]:
            continue

        for date_column in ["매입일", "폐기일", "계약일", "설치일", "계약시작", "렌탈만료"]:
            raw[date_column] = normalize_date_text(raw.get(date_column, ""))

        assets.append(SourceAsset(excel_row=int(row_index) + 5, raw=raw))

    return assets


def read_database_assets(database_path: Path | None) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    if database_path is None or not database_path.exists():
        return [], []

    con = sqlite3.connect(database_path)
    con.row_factory = sqlite3.Row
    try:
        tables = {
            row["name"]
            for row in con.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()
        }
        if "RentalAssets" not in tables:
            return [], []

        assets = [dict(row) for row in con.execute(ASSET_QUERY).fetchall()]
        customers = [dict(row) for row in con.execute(CUSTOMER_QUERY).fetchall()] if "Customers" in tables else []
        return assets, customers
    finally:
        con.close()


def build_customer_match_status(customer_name: str, customers: list[dict[str, Any]]) -> tuple[str, str, str]:
    if not normalize_text(customer_name):
        return "blank", "", ""

    exact = [customer for customer in customers if normalize_text(customer.get("NameOriginal")) == customer_name]
    if len(exact) == 1:
        return "exact", normalize_text(exact[0].get("Id")), normalize_text(exact[0].get("NameOriginal"))
    if len(exact) > 1:
        return "ambiguous-exact", ";".join(normalize_text(c.get("Id")) for c in exact), ";".join(normalize_text(c.get("NameOriginal")) for c in exact)

    source_key = normalize_customer_key(customer_name)
    loose = [customer for customer in customers if normalize_customer_key(customer.get("NameOriginal")) == source_key]
    if len(loose) == 1:
        return "loose", normalize_text(loose[0].get("Id")), normalize_text(loose[0].get("NameOriginal"))
    if len(loose) > 1:
        return "ambiguous-loose", ";".join(normalize_text(c.get("Id")) for c in loose), ";".join(normalize_text(c.get("NameOriginal")) for c in loose)

    return "missing", "", ""


def db_fee_number(row: dict[str, Any]) -> int | None:
    return parse_money(row.get("MonthlyFee", ""), blank_as_none=True)


def source_to_common_row(asset: SourceAsset) -> dict[str, Any]:
    raw = asset.raw
    return {
        "ExcelRow": asset.excel_row,
        "ManagementId": asset.management_id,
        "ManagementNumber": asset.management_number,
        "SourceManagementCompany": asset.management_company_name,
        "SourceManagementCompanyCode": asset.management_company_code,
        "CurrentLocation": asset.current_location,
        "ItemCategoryName": raw.get("상품분류", ""),
        "Manufacturer": raw.get("제조사", ""),
        "ItemName": raw.get("모델명", ""),
        "MachineNumber": raw.get("기계번호", ""),
        "PurchaseVendor": raw.get("매입처", ""),
        "PurchaseDate": raw.get("매입일", ""),
        "DisposalDate": raw.get("폐기일", ""),
        "PurchasePrice": raw.get("매입가", ""),
        "PurchasePriceNumber": asset.purchase_price_number,
        "SalePrice": raw.get("판매가", ""),
        "CustomerName": raw.get("고객명", ""),
        "InstallLocation": raw.get("설치위치", ""),
        "DepositText": raw.get("보증금", ""),
        "MonthlyFee": raw.get("렌탈요금", ""),
        "MonthlyFeeNumber": asset.monthly_fee_number,
        "ContractMonths": raw.get("계약기간", ""),
        "ContractDate": raw.get("계약일", ""),
        "InstallDate": raw.get("설치일", ""),
        "ContractStartDate": raw.get("계약시작", ""),
        "RentalEndDate": raw.get("렌탈만료", ""),
        "FreeSupplyItems": raw.get("무상품목", ""),
        "PaidSupplyItems": raw.get("유상품목", ""),
        "KLimit": raw.get("K제한", ""),
        "CLimit": raw.get("C제한", ""),
        "KExtra": raw.get("K추가", ""),
        "CExtra": raw.get("C추가", ""),
        "Notes": raw.get("기타사항", ""),
    }


def add_manual_issue(
    rows: list[dict[str, Any]],
    *,
    severity: str,
    reason: str,
    asset: SourceAsset | None = None,
    db_asset: dict[str, Any] | None = None,
    detail: str = "",
) -> None:
    rows.append({
        "Severity": severity,
        "Reason": reason,
        "Detail": detail,
        "ExcelRow": asset.excel_row if asset else "",
        "ManagementId": asset.management_id if asset else normalize_text(db_asset.get("ManagementId") if db_asset else ""),
        "ManagementNumber": asset.management_number if asset else normalize_text(db_asset.get("ManagementNumber") if db_asset else ""),
        "SourceLocation": asset.current_location if asset else "",
        "DbLocation": normalize_text(db_asset.get("CurrentLocation") if db_asset else ""),
        "SourceCustomerName": asset.customer_name if asset else "",
        "DbCustomerName": normalize_text(db_asset.get("CustomerName") if db_asset else ""),
        "SourceMachineNumber": asset.machine_number if asset else "",
        "DbMachineNumber": normalize_text(db_asset.get("MachineNumber") if db_asset else ""),
        "DbAssetId": normalize_text(db_asset.get("Id") if db_asset else ""),
        "DbCustomerId": normalize_text(db_asset.get("CustomerId") if db_asset else ""),
        "DbBillingProfileId": normalize_text(db_asset.get("BillingProfileId") if db_asset else ""),
        "DbAssignmentHistoryCount": normalize_text(db_asset.get("AssignmentHistoryCount") if db_asset else ""),
    })


def compare_field(
    diffs: list[dict[str, Any]],
    *,
    management_number: str,
    db_asset_id: str,
    field: str,
    source_value: Any,
    db_value: Any,
    severity: str,
    compare_as: str = "text",
) -> bool:
    if compare_as == "money":
        left = parse_money(source_value, blank_as_none=True)
        right = parse_money(db_value, blank_as_none=True)
        equal = (left or 0) == (right or 0)
    elif compare_as == "customer":
        equal = normalize_customer_key(source_value) == normalize_customer_key(db_value)
    elif compare_as == "serial":
        equal = normalize_unknown_serial(source_value) == normalize_unknown_serial(db_value)
    elif compare_as == "date":
        equal = normalize_date_text(source_value) == normalize_date_text(db_value)
    else:
        equal = normalize_text(source_value) == normalize_text(db_value)

    if not equal:
        diffs.append({
            "Severity": severity,
            "ManagementNumber": management_number,
            "DbAssetId": db_asset_id,
            "Field": field,
            "SourceValue": normalize_text(source_value),
            "DbValue": normalize_text(db_value),
            "CompareMode": compare_as,
        })
    return not equal


def write_csv(path: Path, rows: Iterable[dict[str, Any]], fieldnames: list[str] | None = None) -> None:
    rows = list(rows)
    if fieldnames is None:
        fieldnames = sorted({key for row in rows for key in row.keys()}) if rows else ["Empty"]
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as fp:
        writer = csv.DictWriter(fp, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def analyze(source_assets: list[SourceAsset], db_assets: list[dict[str, Any]], customers: list[dict[str, Any]]) -> dict[str, Any]:
    source_by_number = {asset.management_number: asset for asset in source_assets}
    db_by_number = {normalize_text(asset.get("ManagementNumber")): asset for asset in db_assets if normalize_text(asset.get("ManagementNumber"))}

    matched_numbers = sorted(set(source_by_number) & set(db_by_number))
    new_numbers = sorted(set(source_by_number) - set(db_by_number))
    db_only_numbers = sorted(set(db_by_number) - set(source_by_number))

    update_rows: list[dict[str, Any]] = []
    new_rows: list[dict[str, Any]] = []
    db_only_rows: list[dict[str, Any]] = []
    manual_rows: list[dict[str, Any]] = []
    diff_rows: list[dict[str, Any]] = []
    source_quality_rows: list[dict[str, Any]] = []

    serial_counts: dict[str, int] = {}
    for asset in source_assets:
        serial = normalize_text(asset.machine_number)
        if serial:
            serial_counts[serial] = serial_counts.get(serial, 0) + 1

    for asset in source_assets:
        quality_reasons: list[tuple[str, str]] = []
        if asset.current_location == "렌탈":
            if not asset.customer_name:
                quality_reasons.append(("High", "렌탈 상태인데 고객명이 비어 있음"))
            if not normalize_unknown_serial(asset.machine_number):
                quality_reasons.append(("Medium", "렌탈 상태인데 기계번호/시리얼이 비어 있거나 미상"))
            if not asset.raw.get("계약시작", ""):
                quality_reasons.append(("Medium", "렌탈 상태인데 계약시작이 비어 있음"))
            if not asset.raw.get("렌탈만료", ""):
                quality_reasons.append(("Medium", "렌탈 상태인데 렌탈만료가 비어 있음"))
            if not asset.raw.get("렌탈요금", ""):
                quality_reasons.append(("Low", "렌탈 상태인데 렌탈요금이 공란"))

        if asset.current_location == "창고" and (asset.customer_name or asset.raw.get("렌탈요금", "")):
            quality_reasons.append(("High", "창고 상태인데 고객명 또는 렌탈요금이 남아 있음"))
        if asset.current_location == "폐기" and asset.customer_name:
            quality_reasons.append(("High", "폐기 상태인데 고객명이 남아 있음"))
        if normalize_text(asset.machine_number) and serial_counts.get(normalize_text(asset.machine_number), 0) > 1:
            quality_reasons.append(("Low", "기계번호가 원장 안에서 중복됨"))

        for severity, reason in quality_reasons:
            row = source_to_common_row(asset)
            row["Severity"] = severity
            row["Reason"] = reason
            source_quality_rows.append(row)
            add_manual_issue(manual_rows, severity=severity, reason=reason, asset=asset)

    for number in matched_numbers:
        source = source_by_number[number]
        db_asset = db_by_number[number]
        source_row = source_to_common_row(source)
        status, customer_id, customer_name = build_customer_match_status(source.customer_name, customers)
        row = {
            **source_row,
            "Action": "update-existing",
            "DbAssetId": normalize_text(db_asset.get("Id")),
            "DbManagementId": normalize_text(db_asset.get("ManagementId")),
            "DbManagementCompanyCode": normalize_text(db_asset.get("ManagementCompanyCode")),
            "DbCurrentLocation": normalize_text(db_asset.get("CurrentLocation")),
            "DbCustomerName": normalize_text(db_asset.get("CustomerName")),
            "DbMachineNumber": normalize_text(db_asset.get("MachineNumber")),
            "DbMonthlyFee": normalize_text(db_asset.get("MonthlyFee")),
            "DbCustomerId": normalize_text(db_asset.get("CustomerId")),
            "DbBillingProfileId": normalize_text(db_asset.get("BillingProfileId")),
            "DbAssignmentHistoryCount": normalize_text(db_asset.get("AssignmentHistoryCount")),
            "DbCurrentAssignmentHistoryCount": normalize_text(db_asset.get("CurrentAssignmentHistoryCount")),
            "SourceCustomerMatchStatus": status,
            "MatchedCustomerId": customer_id,
            "MatchedCustomerName": customer_name,
        }
        update_rows.append(row)

        db_asset_id = normalize_text(db_asset.get("Id"))
        linked = is_truthy_database_value(db_asset.get("CustomerId")) or is_truthy_database_value(db_asset.get("BillingProfileId"))
        if compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="ManagementCompanyCode", source_value=source.management_company_code, db_value=db_asset.get("ManagementCompanyCode"), severity="High"):
            add_manual_issue(manual_rows, severity="High", reason="관리업체 코드가 DB와 원장 간 다름", asset=source, db_asset=db_asset)
        if compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="CurrentLocation", source_value=source.current_location, db_value=db_asset.get("CurrentLocation"), severity="High" if linked else "Medium"):
            add_manual_issue(manual_rows, severity="High" if linked else "Medium", reason="현재위치가 DB와 원장 간 다름", asset=source, db_asset=db_asset)
        if compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="CustomerName", source_value=source.customer_name, db_value=db_asset.get("CustomerName"), severity="Medium", compare_as="customer"):
            add_manual_issue(manual_rows, severity="Medium", reason="고객명이 DB와 원장 간 다름", asset=source, db_asset=db_asset)
        if compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="MachineNumber", source_value=source.machine_number, db_value=db_asset.get("MachineNumber"), severity="High", compare_as="serial"):
            add_manual_issue(manual_rows, severity="High", reason="기계번호/시리얼이 DB와 원장 간 다름", asset=source, db_asset=db_asset)
        if compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="MonthlyFee", source_value=source.raw.get("렌탈요금", ""), db_value=db_asset.get("MonthlyFee"), severity="Medium", compare_as="money"):
            add_manual_issue(manual_rows, severity="Medium", reason="렌탈요금이 DB와 원장 간 다름", asset=source, db_asset=db_asset)
        compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="ContractStartDate", source_value=source.raw.get("계약시작", ""), db_value=db_asset.get("ContractStartDate"), severity="Low", compare_as="date")
        compare_field(diff_rows, management_number=number, db_asset_id=db_asset_id, field="RentalEndDate", source_value=source.raw.get("렌탈만료", ""), db_value=db_asset.get("RentalEndDate"), severity="Low", compare_as="date")

        if linked and source.current_location != "렌탈":
            add_manual_issue(
                manual_rows,
                severity="High",
                reason="DB에는 거래처/청구프로필 연결이 있는데 원장 현재위치가 렌탈이 아님",
                asset=source,
                db_asset=db_asset,
            )

    for number in new_numbers:
        source = source_by_number[number]
        status, customer_id, customer_name = build_customer_match_status(source.customer_name, customers)
        row = {
            **source_to_common_row(source),
            "Action": "create-new",
            "SourceCustomerMatchStatus": status,
            "MatchedCustomerId": customer_id,
            "MatchedCustomerName": customer_name,
        }
        new_rows.append(row)
        if source.current_location == "렌탈" and status in {"missing", "ambiguous-exact", "ambiguous-loose"}:
            add_manual_issue(
                manual_rows,
                severity="High",
                reason=f"신규 렌탈 자산의 거래처 매칭 상태 확인 필요: {status}",
                asset=source,
                detail=customer_name,
            )

    for number in db_only_numbers:
        db_asset = db_by_number[number]
        row = {
            "Action": "db-only-review",
            "DbAssetId": normalize_text(db_asset.get("Id")),
            "ManagementId": normalize_text(db_asset.get("ManagementId")),
            "ManagementNumber": normalize_text(db_asset.get("ManagementNumber")),
            "DbManagementCompanyCode": normalize_text(db_asset.get("ManagementCompanyCode")),
            "DbCurrentLocation": normalize_text(db_asset.get("CurrentLocation")),
            "DbCustomerName": normalize_text(db_asset.get("CustomerName")),
            "DbMachineNumber": normalize_text(db_asset.get("MachineNumber")),
            "DbMonthlyFee": normalize_text(db_asset.get("MonthlyFee")),
            "DbCustomerId": normalize_text(db_asset.get("CustomerId")),
            "DbBillingProfileId": normalize_text(db_asset.get("BillingProfileId")),
            "DbAssignmentHistoryCount": normalize_text(db_asset.get("AssignmentHistoryCount")),
            "DbCurrentAssignmentHistoryCount": normalize_text(db_asset.get("CurrentAssignmentHistoryCount")),
        }
        db_only_rows.append(row)
        add_manual_issue(manual_rows, severity="High", reason="DB에는 있으나 원장에는 없는 자산", db_asset=db_asset)

    def sum_source_fee(rows: Iterable[SourceAsset]) -> int:
        return sum(asset.monthly_fee_number or 0 for asset in rows)

    rental_assets = [asset for asset in source_assets if asset.current_location == "렌탈"]
    summary = {
        "sourceAssetRows": len(source_assets),
        "sourceRentalRows": len(rental_assets),
        "sourceWarehouseRows": sum(1 for asset in source_assets if asset.current_location == "창고"),
        "sourceDisposedRows": sum(1 for asset in source_assets if asset.current_location == "폐기"),
        "sourceSoldRows": sum(1 for asset in source_assets if asset.current_location == "판매"),
        "sourceMonthlyFeeSum": sum_source_fee(source_assets),
        "sourceRentalMonthlyFeeSum": sum_source_fee(rental_assets),
        "dbAssetRows": len(db_assets),
        "matchedByManagementNumber": len(matched_numbers),
        "newAssetCandidates": len(new_rows),
        "dbOnlyAssets": len(db_only_rows),
        "manualReviewRows": len(manual_rows),
        "fieldDifferenceRows": len(diff_rows),
        "sourceQualityIssueRows": len(source_quality_rows),
        "managementCompanyCounts": count_by(source_assets, lambda asset: asset.management_company_name),
        "sourceStatusCounts": count_by(source_assets, lambda asset: asset.current_location),
        "sourceCategoryCountsTop20": dict(sorted(count_by(source_assets, lambda asset: asset.raw.get("상품분류", "")).items(), key=lambda item: item[1], reverse=True)[:20]),
    }

    return {
        "summary": summary,
        "updateRows": update_rows,
        "newRows": new_rows,
        "dbOnlyRows": db_only_rows,
        "manualRows": dedupe_manual_rows(manual_rows),
        "diffRows": diff_rows,
        "sourceQualityRows": source_quality_rows,
    }


def count_by(items: Iterable[Any], selector) -> dict[str, int]:
    counts: dict[str, int] = {}
    for item in items:
        key = normalize_text(selector(item))
        counts[key] = counts.get(key, 0) + 1
    return counts


def dedupe_manual_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[tuple[str, str, str, str, str]] = set()
    deduped: list[dict[str, Any]] = []
    for row in rows:
        key = (
            normalize_text(row.get("Severity")),
            normalize_text(row.get("Reason")),
            normalize_text(row.get("ManagementNumber")),
            normalize_text(row.get("DbAssetId")),
            normalize_text(row.get("Detail")),
        )
        if key in seen:
            continue
        seen.add(key)
        deduped.append(row)
    return deduped


def write_markdown_report(
    path: Path,
    *,
    workbook_path: str,
    sheet_name: str,
    database_path: str,
    analysis: dict[str, Any],
    generated_at: datetime,
) -> None:
    summary = analysis["summary"]
    lines = [
        "# 렌탈재고관리 가져오기 프리뷰",
        "",
        f"- 생성시각: {generated_at.strftime('%Y-%m-%d %H:%M:%S')}",
        f"- 원장 파일: `{workbook_path}`",
        f"- 시트: `{sheet_name}`",
        f"- 비교 DB: `{database_path or '미지정 - 원장 품질만 분석'}`",
        "- DB 변경 여부: **없음(읽기 전용)**",
        "",
        "## 1. 요약",
        "",
        "| 항목 | 건수/금액 |",
        "| --- | ---: |",
        f"| 원장 자산 행 | {summary['sourceAssetRows']:,} |",
        f"| 원장 렌탈 상태 | {summary['sourceRentalRows']:,} |",
        f"| 원장 창고 상태 | {summary['sourceWarehouseRows']:,} |",
        f"| 원장 폐기 상태 | {summary['sourceDisposedRows']:,} |",
        f"| 원장 판매 상태 | {summary['sourceSoldRows']:,} |",
        f"| 원장 렌탈료 합계 | {summary['sourceMonthlyFeeSum']:,} |",
        f"| 원장 렌탈 상태 렌탈료 합계 | {summary['sourceRentalMonthlyFeeSum']:,} |",
        f"| DB 렌탈자산 행 | {summary['dbAssetRows']:,} |",
        f"| 관리번호 기준 기존 갱신 후보 | {summary['matchedByManagementNumber']:,} |",
        f"| 신규 추가 후보 | {summary['newAssetCandidates']:,} |",
        f"| DB에만 있는 자산 | {summary['dbOnlyAssets']:,} |",
        f"| 수동검토 후보 | {summary['manualReviewRows']:,} |",
        f"| 필드 차이 | {summary['fieldDifferenceRows']:,} |",
        "",
        "## 2. 관리업체 분포",
        "",
        "| 관리업체 | 건수 |",
        "| --- | ---: |",
    ]
    for key, value in summary["managementCompanyCounts"].items():
        lines.append(f"| {key or '(공란)'} | {value:,} |")

    lines.extend([
        "",
        "## 3. 상태 분포",
        "",
        "| 현재위치 | 건수 |",
        "| --- | ---: |",
    ])
    for key, value in summary["sourceStatusCounts"].items():
        lines.append(f"| {key or '(공란)'} | {value:,} |")

    lines.extend([
        "",
        "## 4. 생성된 파일",
        "",
        "- `existing-update-candidates.csv`: 기존 자산 갱신 후보",
        "- `new-asset-candidates.csv`: 신규 자산 생성 후보",
        "- `db-only-assets.csv`: DB에는 있으나 원장에는 없는 자산",
        "- `manual-review-candidates.csv`: 자동 반영 전 수동검토 대상",
        "- `field-differences.csv`: 원장/DB 필드 차이 상세",
        "- `source-quality-issues.csv`: 원장 자체 품질 이슈",
        "- `summary.json`: 집계값 원본",
        "",
        "## 5. 권장 반영 순서",
        "",
        "1. `manual-review-candidates.csv`의 High 항목을 먼저 정리한다.",
        "2. 기존 갱신 후보는 `RentalAsset.Id`를 유지하고 관리번호 기준으로 필드만 갱신한다.",
        "3. 신규 추가 후보는 새 `RentalAsset.Id`를 생성하되, 거래처 매칭 상태가 `missing` 또는 `ambiguous-*`인 건은 CustomerId를 자동 연결하지 않는다.",
        "4. 청구 프로필 연결은 자산 최신화 후 별도 프리뷰로 처리한다.",
        "5. 실제 반영 전 테스트 DB 복제본에서 갱신 전/후 자산 수, 청구 프로필 연결 수, current assignment history 수를 비교한다.",
    ])
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description="렌탈재고관리 시트와 거래플랜 렌탈자산 DB를 읽기 전용으로 비교합니다.")
    parser.add_argument("--source-tsv", required=True, type=Path)
    parser.add_argument("--database", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--workbook", default="")
    parser.add_argument("--sheet", default="렌탈재고관리")
    args = parser.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    source_assets = read_source_assets(args.source_tsv)
    db_assets, customers = read_database_assets(args.database)
    analysis = analyze(source_assets, db_assets, customers)

    write_csv(args.output / "existing-update-candidates.csv", analysis["updateRows"])
    write_csv(args.output / "new-asset-candidates.csv", analysis["newRows"])
    write_csv(args.output / "db-only-assets.csv", analysis["dbOnlyRows"])
    write_csv(args.output / "manual-review-candidates.csv", analysis["manualRows"])
    write_csv(args.output / "field-differences.csv", analysis["diffRows"])
    write_csv(args.output / "source-quality-issues.csv", analysis["sourceQualityRows"])

    (args.output / "summary.json").write_text(
        json.dumps(analysis["summary"], ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    report_path = args.output / "rental-inventory-import-preview.md"
    write_markdown_report(
        report_path,
        workbook_path=args.workbook,
        sheet_name=args.sheet,
        database_path=str(args.database) if args.database else "",
        analysis=analysis,
        generated_at=datetime.now(),
    )

    print(f"preview_report={report_path}")
    print(f"source_assets={analysis['summary']['sourceAssetRows']}")
    print(f"matched={analysis['summary']['matchedByManagementNumber']}")
    print(f"new_assets={analysis['summary']['newAssetCandidates']}")
    print(f"manual_review={analysis['summary']['manualReviewRows']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

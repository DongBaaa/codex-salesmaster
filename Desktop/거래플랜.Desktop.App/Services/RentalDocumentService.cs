using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record RentalReturnReportFields(string ReturnReason, string FaultDescription);

public sealed class RentalDocumentService
{
    private const double A4Width = 793.7;
    private const double A4Height = 1122.5;
    private const double PageMargin = 28d;
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(186, 186, 186));
    private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(246, 246, 246));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(102, 102, 102));
    private static readonly Brush LightAccentBrush = new SolidColorBrush(Color.FromRgb(240, 240, 240));

    public FixedDocument BuildEquipmentDetailDocument(
        IReadOnlyList<LocalRentalAsset> assets,
        LocalCustomer? customer,
        LocalCompanyProfile? companyProfile,
        IReadOnlyDictionary<string, string>? managementCompanyNames = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0)
            throw new ArgumentException("렌탈장비내역서를 만들 장비가 없습니다.", nameof(assets));

        const double pageWidth = A4Height;
        const double pageHeight = A4Width;
        const int firstPageRowLimit = 18;
        const int continuationRowLimit = 22;

        var orderedAssets = assets
            .OrderBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var chunks = new List<List<LocalRentalAsset>>();
        var skip = 0;
        var take = Math.Min(firstPageRowLimit, orderedAssets.Count);
        chunks.Add(orderedAssets.Take(take).ToList());
        skip += take;
        while (skip < orderedAssets.Count)
        {
            take = Math.Min(continuationRowLimit, orderedAssets.Count - skip);
            chunks.Add(orderedAssets.Skip(skip).Take(take).ToList());
            skip += take;
        }

        var anchor = orderedAssets[0];
        var document = CreateDocument(pageWidth, pageHeight);
        for (var pageIndex = 0; pageIndex < chunks.Count; pageIndex++)
        {
            var page = CreatePage(pageWidth, pageHeight);
            var root = CreateRootGrid(pageWidth, pageHeight);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddToGrid(root, CreateEquipmentDetailTitle(anchor, companyProfile, managementCompanyNames, pageIndex + 1, chunks.Count), 0);

            if (pageIndex == 0)
                AddToGrid(root, CreateEquipmentDetailSummary(anchor, customer, companyProfile, managementCompanyNames, orderedAssets.Count), 1);
            else
                AddToGrid(root, CreateEquipmentContinuationSummary(anchor, customer, companyProfile, managementCompanyNames), 1);

            AddToGrid(root, CreateEquipmentDetailTable(chunks[pageIndex], pageIndex == 0 ? 1 : (pageIndex * continuationRowLimit) - (continuationRowLimit - firstPageRowLimit) + 1), 2);

            var note = CreateText(
                "※ 월 제한매수 / 추가요금 정보는 현재 렌탈 자산 저장값 기준으로 표시하며, 미입력 항목은 '-' 로 표기합니다.",
                10.5,
                FontWeights.Normal,
                Brushes.Gray);
            note.Margin = new Thickness(0, 8, 0, 0);
            AddToGrid(root, note, 4);

            page.Children.Add(root);
            document.Pages.Add(WrapPage(page));
        }

        return document;
    }

    public FixedDocument BuildReturnReportDocument(
        LocalRentalAsset asset,
        LocalCompanyProfile? companyProfile,
        RentalReturnReportFields? reportFields = null,
        IReadOnlyDictionary<string, string>? managementCompanyNames = null)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var document = CreateDocument();
        var page = CreatePage();
        var root = CreateRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(CreateText("회수 장비 점검 내역서", 24, FontWeights.Bold, Brushes.Black, TextAlignment.Left));
        titleStack.Children.Add(CreateText("■ 카운터 리포트와 함께 장비 붙여 주세요. ■", 11, FontWeights.SemiBold, Brushes.DarkRed));
        titleGrid.Children.Add(titleStack);

        var approvalTable = CreateApprovalTable();
        approvalTable.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(approvalTable, 1);
        titleGrid.Children.Add(approvalTable);
        AddToGrid(root, titleGrid, 0);

        AddToGrid(root, CreateTwoColumnSummary(
            ("관리번호", Coalesce(asset.ManagementNumber, asset.ManagementId)),
            ("제조사", asset.Manufacturer),
            ("품명", asset.ItemName),
            ("제품번호", asset.MachineNumber),
            ("렌탈업체", ResolveAssetManagementCompanyName(asset, managementCompanyNames)),
            ("구입일자", FormatDate(asset.PurchaseDate)),
            ("거래처명", asset.CustomerName),
            ("회수일자", FormatDate(asset.DisposalDate ?? asset.RentalEndDate)),
            ("담당지점", ResolveAssetResponsibleOfficeName(asset)),
            ("회수이유", reportFields is null ? BuildReturnReason(asset) : reportFields.ReturnReason)), 1);

        AddToGrid(root, CreateLabeledMultilineBlock("장애내용(상세하게 기록)", reportFields?.FaultDescription ?? string.Empty, 80), 2);

        var inspectionGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        inspectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        inspectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inspectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        inspectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        inspectionGrid.Children.Add(CreateBorderedBlock("점검상황", "미처리   /   처리중   /   완료", 0));
        inspectionGrid.Children.Add(CreateBorderedBlock("판매/임대 가능", BuildSaleableState(asset), 1));
        inspectionGrid.Children.Add(CreateBorderedBlock("폐기", string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase) ? "예정" : "-", 2));
        inspectionGrid.Children.Add(CreateBorderedBlock("보류[이유]", string.Equals(asset.AssetStatus, "대기", StringComparison.OrdinalIgnoreCase) ? asset.Notes : string.Empty, 3));
        AddToGrid(root, inspectionGrid, 3);

        AddToGrid(root, CreateCounterTable(), 4);
        AddToGrid(root, CreatePartsTable(), 5);
        AddToGrid(root, CreateLabeledMultilineBlock("처리내역[교환부품]", string.Empty, 90), 6);

        var memoBorder = CreateBorder();
        memoBorder.Padding = new Thickness(14, 12, 14, 12);
        memoBorder.Child = new StackPanel
        {
            Children =
            {
                CreateText("참고", 13, FontWeights.Bold, AccentBrush),
                CreateText($"- 현재 자산 상태: {Coalesce(asset.AssetStatus, "미입력")}", 11, FontWeights.Normal, Brushes.Black),
                CreateText($"- 계약기간: {BuildContractPeriod(asset)}", 11, FontWeights.Normal, Brushes.Black),
                CreateText($"- 설치위치: {Coalesce(asset.InstallLocation, "미입력")}", 11, FontWeights.Normal, Brushes.Black),
                CreateText($"- 작성 기준 회사설정: {companyProfile?.TradeName ?? "미지정"}", 11, FontWeights.Normal, Brushes.Black)
            }
        };
        AddToGrid(root, memoBorder, 7);

        page.Children.Add(root);
        document.Pages.Add(WrapPage(page));
        return document;
    }

    public RentalContractDocumentModel CreateContractDocumentModel(
        LocalRentalAsset asset,
        LocalCustomer? customer,
        LocalCompanyProfile companyProfile,
        DateOnly? preferredCustomerContractDate = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(companyProfile);

        var tenantName = Coalesce(customer?.NameOriginal, asset.CustomerName, "거래처 미지정");
        var companyName = Coalesce(companyProfile.TradeName, ResolveOfficeName(companyProfile.OfficeCode, companyProfile), "관리업체");
        var contractDates = RentalContractDateRules.Resolve(
            preferredCustomerContractDate,
            asset.ContractDate,
            asset.ContractStartDate,
            asset.InstallDate);
        var contractDate = contractDates.ContractDate;
        var startDate = contractDates.ContractStartDate;
        var endDate = asset.RentalEndDate;

        var model = new RentalContractDocumentModel
        {
            TenantName = tenantName,
            TenantBusinessNumber = Coalesce(customer?.BusinessNumber),
            TenantAddress = BuildCustomerAddress(customer),
            TenantRepresentative = Coalesce(customer?.Representative, customer?.ContactPerson),
            CompanyName = companyName,
            CompanyBusinessNumber = Coalesce(companyProfile.BusinessNumber),
            CompanyAddress = Coalesce(companyProfile.Address),
            CompanyRepresentative = Coalesce(companyProfile.Representative),
            CompanyContactNumber = ResolveContractPhoneNumber(companyProfile),
            CompanyFaxNumber = ResolveContractFaxNumber(companyProfile),
            ManagementNumber = Coalesce(asset.ManagementNumber, asset.ManagementId, "미입력"),
            ItemName = Coalesce(asset.ItemName, "미입력"),
            MachineNumber = Coalesce(asset.MachineNumber, "미입력"),
            DepositText = string.IsNullOrWhiteSpace(asset.DepositText) ? "면제" : asset.DepositText.Trim(),
            MonthlyFee = asset.MonthlyFee,
            InstallLocation = Coalesce(asset.InstallLocation, "미입력"),
            ContractDate = contractDate,
            ContractStartDate = startDate,
            ContractEndDate = endDate,
            IntroText = $"{tenantName} 과(와) {companyName}는 아래의 본 계약에 명시된 장비에 대하여 렌탈 및 유지관리 지원을 위하여 아래와 같이 렌탈기기의 렌탈 계약을 체결한다.",
            ClosingLine1 = "상기 계약 체결의 증거로서 본 사무기기 렌탈 계약서 2통을 작성하여",
            ClosingLine2 = $"{tenantName} 와(과) {companyName} 기명 날인 후 각 1통씩 보관한다.",
            CompanyStampImage = companyProfile.StampImage
        };

        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 1 조",
            Title = "렌탈 계약의 목적",
            Body = $"본 계약은 {tenantName}에서 {companyName}로 부터 렌탈한 사무기기가 정상적으로 사용될 수 있도록 렌탈기기의 가동에 필요한 일체의 소모품 제공 및 보수 등을 {companyName}에서 행하며, {tenantName}는(은) 이에 대하여 소정의 렌탈 요금을 {companyName}에게 지불하는 절차와 이행 사항의 내용을 규정하는 것을 목적으로 한다."
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 2 조",
            Title = "렌탈 사무기기",
            Body = "(1) 별첨 렌탈장비내역서 참조"
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 3 조",
            Title = "계약기간",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) 본 계약의 유효 기간은 {FormatDateWithKorean(startDate)} 로 부터 {FormatDateWithKorean(endDate)} 까지로 한다.",
                "(2) 계약 기간 만료일 1개월 전에 서면에 의한 해지통보가 없는 때에는 본 계약과 동일한 조건으로 2년간 계약 기간이 자동 연장되는 것으로 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 4 조",
            Title = "보증금 및 렌탈 요금",
            Body = string.Join(Environment.NewLine, new[]
            {
                "보증금 및 렌탈 요금은 다음과 같이 정한다.",
                $"(1) {tenantName}는(은) {companyName}에게 렌탈 계약 장비에 대한 보증금({model.DepositText})을 현금으로 지불 하여야 한다. 보증금에 대한 이자는 계산하지 아니한다.(관공서의 경우는 보증금 지불을 예외로 적용한다.)",
                $"(2) 계약 기간의 만료 또는 해지의 경우에 {companyName}는 {tenantName}에게 보증금 전액을 반환한다. 단, {tenantName}의 {companyName}에 대한 채무가 있는 때에는 이를 보증금과 우선적으로 상계한다.",
                "(3) 다수의 사무기기 렌탈의 보증금 및 렌탈 요금은 별첨 렌탈장비내역서 참조"
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 5 조",
            Title = "렌탈 요금의 지불",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) 렌탈 요금은 매월 {companyName}의 청구일로 부터 15일 이내에 현금 또는 카드로 지불 한다.",
                $"(2) {tenantName}에서 렌탈 요금 지불을 지체한 때에는 {companyName}는 별도의 통보 없이 연체 할증료를 청구할 수 있으며 연체 할증 요율은 청구 금액의 연 20%이내로 한다.",
                $"(3) 모든 지불은 {companyName}가 별도 통보하지 않는 한 {companyName}의 본.지사 영업소 또는 {companyName}의 지정 금융 기관에 납부한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 6 조",
            Title = "렌탈 요금의 계산",
            Body = string.Join(Environment.NewLine, new[]
            {
                "(1) 렌탈 요금 계산은 아래와 같이 한다.",
                "(2) 렌탈 요금 계산 기간이 1개월 미만이며 기본 매수에 미달할 경우의 최초 월은 일할 계산하며 최종 월은 기본 임차요금으로 계산한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 7 조",
            Title = "유지보수 및 사무기기 교체",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) {companyName}는 {tenantName}에게 렌탈기기 사용 방법을 지도하여야 한다.",
                $"(2) 보수SERVICE, 소모품 제공 등 일체의 작업 실시는 {companyName}의 영업 시간내(평일09:00~18:00 / 토,일,공휴일은 휴무)로 정한다. 단, 부득이한 사유로 {companyName}의 영업 시간 외에 실시하는 때에는 별도로 {companyName}는 소정의 보수SERVICE 요금을 청구할 수 있다.",
                $"(3) {tenantName}의 고의 또는 과실로 인한 고장 수리에 대하여 별도로 {companyName}는 소정의 보수SERVICE 요금을 청구 할 수 있다.",
                $"(4) 렌탈 사용중인 사무기기의 고장 또는 노후화로 사무기기를 교체해야 할 경우 {companyName}는 동일 기종 또는 기존 사무기기 성능 이상의 사무기기로 무상 교체하며 계약 기간은 제3조 1항의 일자로 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 8 조",
            Title = "소모품",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) {companyName}는 {tenantName}에서 렌탈기기를 원할하게 사용할 수 있도록 적절량의 소모품을 {tenantName}에게 제공하여야 한다.",
                $"(2) 위 소모품의 소유권은 {companyName}에게 있고 {tenantName}는(은) 이를 선량한 관리자로서의 주의 의무를 다하여 관리 사용하여야 한다.",
                $"(3) {tenantName}는(은) 위 소모품이 {companyName}의 소유임을 표시한 표식을 훼손 또는 임의의 매각, 대여, 유용 기타 이와 유사한 일체의 행위를 하여서는 아니된다.",
                "(4) 위 소모품은 해당 계약 렌탈기기에만 사용하여야 한다.",
                $"(5) 보수SERVICE시에 교환된 소모품, 부품등은 {companyName}가 이를 회수한다.",
                $"(6) {tenantName}는(은) 압류, 가압류, 공조.공과의 체납 기타 이와 유사한 사유가 발생한 때에는 지체없이 그 사실을 {companyName}에게 통지하고 {companyName}의 조치에 따라야 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 9 조",
            Title = "설치 장소의 변경",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) {tenantName}는(은) 본 렌탈기기를 최초 설치 장소에서 사용함을 원칙으로 한다. 단, 설치 장소를 변경하고자 하는 때에는 {companyName}와 협의 하여야 하며 이동 설치는 {companyName}에서 실시한다.",
                $"(2) 또한 본 렌탈기기의 이동 설치에 따른 소정의 실비가 발생할 수 있으며, 일체의 경비는 {tenantName}가 {companyName}에게 지불하여야 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 10 조",
            Title = "사용 목적의 변경",
            Body = $"{tenantName}는(은) 본 렌탈기기로 인쇄(복사) 영업을 하고자 하는 때에는 문서로 {companyName}의 승인을 받아야 하며, {companyName}에서 별도 정하는 규정을 준수 하여야 한다."
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 11 조",
            Title = "사무기기 유지보수",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"복합기 사용과 관련하여 렌탈기기 소프트웨어[드라이버]의 컴퓨터 설치 지원을 하나 윈도우의 재설치나 부품 교체는 실비를 청구한다. 또한 아래 각 호의 경우 {companyName}는 실비를 청구할 수 있다.",
                "① 사용자가 소프트웨어를 삭제한 경우.",
                "② 사용자의 컴퓨터 교체나 포맷으로 인한 소프트웨어 재설치를 요청할 경우."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 12 조",
            Title = "계약 항목의 변경",
            Body = $"본 계약의 조항 변경을 요하는 때에는 1개월 전에 {tenantName}와(과) {companyName}간에 협의하여 변경할 수 있다."
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 13 조",
            Title = "해지",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) {tenantName} 또는 {companyName}에서 본 계약을 해지 하고자 하는 때에는 해지 예정일 1개월 전에 문서로써 상대방에 통지 하여야 한다.",
                $"(2) {tenantName}에게 다음 각호중 하나에 해당하는 사유가 발생 하였을때 {companyName}는 {tenantName}에게 사전 통보 없이 즉시 본 계약을 해지할 수 있다.",
                "① 본 계약을 이행하지 아니한 때.",
                "② 압류, 가압류, 가처분, 경매, 공과의 체납처분 및 이와 유사한 행위가 있어 계약의 이행을 할 수 없다고 판단될 때",
                "③ 당좌 거래 정지 처분이 있을 때",
                "④ 회생, 해산, 파산 절차 개시에 들어간 경우.",
                $"(3) 본 계약 기간이 만료 되거나 해지되는 때에는 {tenantName}는(은) {companyName}에 대한 일체의 채무를 지체없이 정산하여야 하며, 렌탈기기 및 잔여 소모품등을 반환 하여야 한다.",
                $"(4) {tenantName}는(은) {companyName}의 귀책 사유가 없이 본 계약을 준수하지 아니하고 일방 해지할 경우, {companyName}에게 렌탈 계약에서 규정한 잔여 렌탈 기간에 월 기본 렌탈 요금을 곱한 금액( 잔여렌탈기간 X 월 기본렌탈 요금 )의 50%를 위약금으로 즉시 지급하여야 하며, {tenantName}는(은) 잔여 소모품등을 {companyName}에게 반환 하여야 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 14 조",
            Title = "손해배상",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) {tenantName}는(은) 본 계약 조항 위반으로 인한 일체의 손해를 {companyName}에게 배상 하여야 한다.",
                $"(2) {tenantName}는(은) 고의 또는 과실로 인하여 렌탈기기에 손해가 발생한 때에는 {companyName}에게 그 손해를 배상하여야 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 15 조",
            Title = "소유권의 귀속 및 양도 등 금지",
            Body = string.Join(Environment.NewLine, new[]
            {
                $"(1) {tenantName}는 {companyName}가 렌탈한 렌탈기기 및 소모품은 {companyName}의 소유임을 확인 한다.",
                $"(2) {tenantName}는(은) 렌탈기기 및 소모품을 무단양도, 질권설정, 양도담보, 대여, 전대하거나 기타 일체의 처분행위를 하여서는 아니된다.",
                $"(3) {tenantName}는(은) {companyName}의 소유권을 침해하는 행위 발생시(가압류, 압류, 가처분, 공과금 체납에 의한 처분등) 즉시 {companyName}에게 통지하고, {companyName}의 소유 재산임을 알리고 입증하여야 한다.",
                $"(4) {tenantName}는(은) 본 조의 의무를 위반 하였을 경우, {companyName}에게 모든 손해를 배상하여야 한다."
            })
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 16 조",
            Title = "기타",
            Body = "위 각 조항에 명시되지 아니한 사항에 관하여는 관계 법령 및 일반 상관례에 의한다."
        });
        model.Clauses.Add(new RentalContractClauseModel
        {
            Number = "제 17 조",
            Title = "합의 관할",
            Body = $"{tenantName}와(과) {companyName}간 본 계약에 관하여 다툼이 있는 때에는 민사소송법상의 관할 법원을 원칙으로 하며 {companyName}의 본, 지사 및 영업소를 관할하는 법원에 제소할 수 있다."
        });

        model.SpecialTerms.Add("1. 사용중 일부 기기 회수 요청시에는 무상으로 렌탈 서비스 하고 있는 기기를 최우선으로 회수 처리 한다.");
        model.SpecialTerms.Add("2. 본 계약에 따라 설치되는 디지털복합기[내장형 저장매체 포함]는 CC인증[CC인증서 첨부]을 통과한 제품입니다.");

        return model;
    }

    public FixedDocument BuildContractDocument(LocalRentalAsset asset, LocalCustomer? customer, LocalCompanyProfile companyProfile)
        => BuildContractDocument(CreateContractDocumentModel(asset, customer, companyProfile));

    public FixedDocument BuildContractDocument(RentalContractDocumentModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var pages = new List<ContractPageContext>();
        var currentPage = CreateContractPageContext(model.Title, false);
        pages.Add(currentPage);

        currentPage = AddContractElement(pages, currentPage, model.Title, CreateContractIntroBlock(model));
        currentPage = AddContractElement(pages, currentPage, model.Title, CreateContractHeaderTable(model));
        currentPage = AddContractElement(pages, currentPage, model.Title, CreateContractAssetSummary(model));

        foreach (var clause in model.Clauses)
            currentPage = AddContractClause(pages, currentPage, model.Title, clause);

        currentPage = AddSpecialTerms(pages, currentPage, model.Title, model.SpecialTerms);
        currentPage = AddContractElement(pages, currentPage, model.Title, CreateContractClosingBlock(model));
        currentPage = AddContractElement(pages, currentPage, model.Title, CreateContractExecutionSection(model));

        var document = CreateDocument();
        for (var index = 0; index < pages.Count; index++)
        {
            pages[index].PageNumberText.Text = $"{index + 1} / {pages.Count}";
            document.Pages.Add(WrapPage(pages[index].Page));
        }

        return document;
    }

    private static ContractPageContext CreateContractPageContext(string title, bool isContinuation)
    {
        var page = CreatePage();
        var root = CreateRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var (header, pageNumberText) = CreateContractPageHeader(title, isContinuation);
        AddToGrid(root, header, 0);

        var bodyPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(bodyPanel, 1);
        root.Children.Add(bodyPanel);

        page.Children.Add(root);

        header.Measure(new Size(root.Width, double.PositiveInfinity));
        var maxBodyHeight = Math.Max(0d, root.Height - header.DesiredSize.Height - bodyPanel.Margin.Top);

        return new ContractPageContext(page, bodyPanel, pageNumberText, root.Width, maxBodyHeight);
    }

    private static ContractPageContext AddContractElement(
        List<ContractPageContext> pages,
        ContractPageContext currentPage,
        string title,
        UIElement element)
    {
        if (!CanFitOnContractPage(currentPage, element))
        {
            currentPage = CreateContractPageContext(title, true);
            pages.Add(currentPage);
        }

        currentPage.Add(element);
        return currentPage;
    }

    private static ContractPageContext AddContractClause(
        List<ContractPageContext> pages,
        ContractPageContext currentPage,
        string title,
        RentalContractClauseModel clause)
    {
        var lines = SplitDocumentLines(clause.Body).ToList();
        if (lines.Count == 0)
            lines.Add(string.Empty);

        var lineIndex = 0;
        while (lineIndex < lines.Count)
        {
            var remainingLines = lines.Skip(lineIndex).ToList();
            var isContinuation = lineIndex > 0;
            var maxLines = GetMaxClauseLinesForPage(currentPage, clause, remainingLines, isContinuation);

            if (maxLines == 0)
            {
                currentPage = CreateContractPageContext(title, true);
                pages.Add(currentPage);
                maxLines = GetMaxClauseLinesForPage(currentPage, clause, remainingLines, isContinuation);
                if (maxLines == 0)
                    maxLines = 1;
            }

            currentPage.Add(CreateClauseFragment(clause, remainingLines.Take(maxLines).ToList(), isContinuation));
            lineIndex += maxLines;

            if (lineIndex < lines.Count)
            {
                currentPage = CreateContractPageContext(title, true);
                pages.Add(currentPage);
            }
        }

        return currentPage;
    }

    private static ContractPageContext AddSpecialTerms(
        List<ContractPageContext> pages,
        ContractPageContext currentPage,
        string title,
        IEnumerable<string> specialTerms)
    {
        var terms = specialTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .ToList();

        if (terms.Count == 0)
            return currentPage;

        var termIndex = 0;
        while (termIndex < terms.Count)
        {
            var remainingTerms = terms.Skip(termIndex).ToList();
            var isContinuation = termIndex > 0;
            var maxTerms = GetMaxSpecialTermsForPage(currentPage, remainingTerms, isContinuation);

            if (maxTerms == 0)
            {
                currentPage = CreateContractPageContext(title, true);
                pages.Add(currentPage);
                maxTerms = GetMaxSpecialTermsForPage(currentPage, remainingTerms, isContinuation);
                if (maxTerms == 0)
                    maxTerms = 1;
            }

            currentPage.Add(CreateSpecialTermsFragment(remainingTerms.Take(maxTerms).ToList(), isContinuation));
            termIndex += maxTerms;

            if (termIndex < terms.Count)
            {
                currentPage = CreateContractPageContext(title, true);
                pages.Add(currentPage);
            }
        }

        return currentPage;
    }

    private static FixedDocument CreateDocument(double pageWidth = A4Width, double pageHeight = A4Height)
    {
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        return document;
    }

    private static FixedPage CreatePage(double pageWidth = A4Width, double pageHeight = A4Height)
        => new()
        {
            Width = pageWidth,
            Height = pageHeight,
            Background = Brushes.White
        };

    private static Grid CreateRootGrid(double pageWidth = A4Width, double pageHeight = A4Height)
        => new()
        {
            Margin = new Thickness(PageMargin),
            Width = pageWidth - (PageMargin * 2),
            Height = pageHeight - (PageMargin * 2)
        };

    private static PageContent WrapPage(FixedPage page)
    {
        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        return content;
    }

    private static void AddToGrid(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private static Border CreateBorder()
        => new()
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brushes.White
        };

    private static TextBlock CreateText(
        string text,
        double fontSize,
        FontWeight weight,
        Brush foreground,
        TextAlignment alignment = TextAlignment.Left)
        => new()
        {
            Text = text,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = foreground,
            TextAlignment = alignment,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

    private static Grid CreateApprovalTable()
    {
        var grid = new Grid
        {
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Right,
            ShowGridLines = false
        };
        for (var i = 0; i < 4; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });

        var labels = new[] { "담당", "과장", "부장", "사장" };
        for (var index = 0; index < labels.Length; index++)
        {
            var header = CreateCell(labels[index], true, TextAlignment.Center, 11);
            Grid.SetColumn(header, index);
            grid.Children.Add(header);

            var body = CreateCell(string.Empty, false, TextAlignment.Center, 12);
            body.Height = 52;
            Grid.SetColumn(body, index);
            Grid.SetRow(body, 1);
            grid.Children.Add(body);
        }

        return grid;
    }

    private static Grid CreateTwoColumnSummary(params (string Label, string Value)[] items)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rowCount = (int)Math.Ceiling(items.Length / 2d);
        for (var i = 0; i < rowCount; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < items.Length; i++)
        {
            var row = i / 2;
            var isLeft = i % 2 == 0;
            var baseColumn = isLeft ? 0 : 3;
            var label = CreateCell(items[i].Label, true, TextAlignment.Center, 11);
            var value = CreateCell(items[i].Value, false, TextAlignment.Left, 11);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, baseColumn);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, baseColumn + 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        return grid;
    }

    private static Border CreateLabeledMultilineBlock(string label, string value, double minHeight)
    {
        var border = CreateBorder();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Border
        {
            Background = HeaderFill,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6, 10, 6),
            Child = CreateText(label, 11, FontWeights.Bold, Brushes.Black)
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var body = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            MinHeight = minHeight,
            Child = CreateText(value, 11, FontWeights.Normal, Brushes.Black)
        };
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        border.Child = grid;
        return border;
    }

    private static Border CreateBorderedBlock(string label, string value, int column)
    {
        var border = CreateBorder();
        border.Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Border
        {
            Background = HeaderFill,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6, 10, 6),
            Child = CreateText(label, 11, FontWeights.Bold, Brushes.Black)
        };
        var body = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 48,
            Child = CreateText(value, 11, FontWeights.Normal, Brushes.Black)
        };
        Grid.SetRow(body, 1);
        grid.Children.Add(header);
        grid.Children.Add(body);
        border.Child = grid;
        Grid.SetColumn(border, column);
        return border;
    }

    private static Grid CreateCounterTable()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var width in new[] { 120d, 80d, 80d, 80d, 80d, 110d, 90d })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(grid, "소모품수명", 0, 0, true);
        AddCell(grid, "색상", 1, 0, true);
        AddCell(grid, "K", 1, 1, true, TextAlignment.Center);
        AddCell(grid, "C", 1, 2, true, TextAlignment.Center);
        AddCell(grid, "M", 1, 3, true, TextAlignment.Center);
        AddCell(grid, "Y", 1, 4, true, TextAlignment.Center);
        AddCell(grid, "메인보드", 1, 5, true);
        AddCell(grid, "유 / 무", 1, 6, false, TextAlignment.Center);

        var rows = new[]
        {
            new[] { "토너", string.Empty, string.Empty, string.Empty, string.Empty, "HDD", "유 / 무" },
            new[] { "드럼", string.Empty, string.Empty, string.Empty, string.Empty, "팩스킷", "유 / 무" },
            new[] { "현상기", string.Empty, string.Empty, string.Empty, string.Empty, "고압보드", "유 / 무" },
            new[] { "벨트", string.Empty, string.Empty, string.Empty, string.Empty, "OP패널", "유 / 무" },
            new[] { "정착기", string.Empty, string.Empty, string.Empty, string.Empty, "롤러", "유 / 무" }
        };

        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        block.Children.Add(CreateText("소모품/부품 현황", 13, FontWeights.Bold, AccentBrush));
        block.Children.Add(grid);

        for (var r = 0; r < rows.Length; r++)
        {
            var rowGrid = new Grid();
            foreach (var width in new[] { 120d, 80d, 80d, 80d, 80d, 110d, 90d })
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            rowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < rows[r].Length; c++)
                AddCell(rowGrid, rows[r][c], 0, c, c == 0 || c == 5, c == 6 ? TextAlignment.Center : TextAlignment.Left);
            block.Children.Add(rowGrid);
        }

        var wrapper = new Grid();
        wrapper.Children.Add(block);
        return wrapper;
    }

    private static Grid CreatePartsTable()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var width in new[] { 120d, 140d, 100d, 100d })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(grid, "사용량[카운터]", 0, 0, true);
        AddCell(grid, "흑백", 1, 0, true, TextAlignment.Center);
        AddCell(grid, "컬러", 1, 1, true, TextAlignment.Center);
        AddCell(grid, "합계", 1, 2, true, TextAlignment.Center);
        AddCell(grid, "-", 1, 3, false, TextAlignment.Center);
        return grid;
    }

    private static void AddCell(Grid grid, string text, int row, int column, bool isHeader, TextAlignment alignment = TextAlignment.Left)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (grid.ColumnDefinitions.Count <= column)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cell = CreateCell(text, isHeader, alignment, 11);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static Border CreateCell(string text, bool isHeader, TextAlignment alignment, double fontSize)
        => new()
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            Background = isHeader ? HeaderFill : Brushes.White,
            Padding = new Thickness(8, 6, 8, 6),
            Child = CreateText(text, fontSize, isHeader ? FontWeights.Bold : FontWeights.Normal, Brushes.Black, alignment)
        };

    private static Border CreateEquipmentCell(string text, bool isHeader, TextAlignment alignment, double fontSize, Thickness padding)
        => new()
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            Background = isHeader ? HeaderFill : Brushes.White,
            Padding = padding,
            Child = CreateEquipmentTableText(text, fontSize, isHeader ? FontWeights.Bold : FontWeights.Normal, alignment)
        };

    private static Border CreateEquipmentAutoShrinkCell(string text, bool isHeader, TextAlignment alignment, double fontSize, Thickness padding)
        => new()
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            Background = isHeader ? HeaderFill : Brushes.White,
            Padding = padding,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = CreateEquipmentTableText(text, fontSize, isHeader ? FontWeights.Bold : FontWeights.Normal, alignment, trim: false)
            }
        };

    private static TextBlock CreateEquipmentTableText(string text, double fontSize, FontWeight weight, TextAlignment alignment, bool trim = true)
        => new()
        {
            Text = text,
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = Brushes.Black,
            TextAlignment = alignment,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static UIElement CreateEquipmentDetailTitle(
        LocalRentalAsset anchor,
        LocalCompanyProfile? companyProfile,
        IReadOnlyDictionary<string, string>? managementCompanyNames,
        int pageNumber,
        int totalPages)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = CreateText("【 별첨 】", 14, FontWeights.Bold, AccentBrush);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var centerStack = new StackPanel();
        centerStack.Children.Add(CreateText("렌탈장비 내역서", 22, FontWeights.Bold, Brushes.Black, TextAlignment.Center));
        centerStack.Children.Add(CreateText(
            $"{ResolveAssetResponsibleOfficeName(anchor)} 렌탈 장비 상세 목록",
            11,
            FontWeights.Normal,
            Brushes.Gray,
            TextAlignment.Center));
        Grid.SetColumn(centerStack, 1);
        grid.Children.Add(centerStack);

        var right = CreateText($"{pageNumber} / {totalPages}", 11, FontWeights.Normal, Brushes.Gray, TextAlignment.Right);
        right.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    private static Border CreateEquipmentDetailSummary(
        LocalRentalAsset anchor,
        LocalCustomer? customer,
        LocalCompanyProfile? companyProfile,
        IReadOnlyDictionary<string, string>? managementCompanyNames,
        int assetCount)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(0);

        var grid = new Grid();
        for (var i = 0; i < 6; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i % 2 == 0 ? new GridLength(98) : new GridLength(1, GridUnitType.Star)
            });
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(grid, "발주거래처", 0, 0, true, TextAlignment.Center);
        AddCell(grid, Coalesce(customer?.NameOriginal, anchor.CustomerName, "미입력"), 0, 1, false);
        AddCell(grid, "렌탈업체", 0, 2, true, TextAlignment.Center);
        AddCell(grid, ResolveAssetManagementCompanyName(anchor, managementCompanyNames), 0, 3, false);
        AddCell(grid, "장비수", 0, 4, true, TextAlignment.Center);
        AddCell(grid, $"{assetCount:N0}대", 0, 5, false, TextAlignment.Center);

        AddCell(grid, "사업자번호", 1, 0, true, TextAlignment.Center);
        AddCell(grid, Coalesce(customer?.BusinessNumber, "미입력"), 1, 1, false);
        AddCell(grid, "담당지점", 1, 2, true, TextAlignment.Center);
        AddCell(grid, ResolveAssetResponsibleOfficeName(anchor), 1, 3, false);
        AddCell(grid, "작성일", 1, 4, true, TextAlignment.Center);
        AddCell(grid, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 1, 5, false, TextAlignment.Center);

        border.Child = grid;
        return border;
    }

    private static Border CreateEquipmentContinuationSummary(
        LocalRentalAsset anchor,
        LocalCustomer? customer,
        LocalCompanyProfile? companyProfile,
        IReadOnlyDictionary<string, string>? managementCompanyNames)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(12, 10, 12, 10);
        border.Child = CreateText(
            $"발주거래처: {Coalesce(customer?.NameOriginal, anchor.CustomerName, "미입력")}    |    렌탈업체: {ResolveAssetManagementCompanyName(anchor, managementCompanyNames)}",
            11.5,
            FontWeights.SemiBold,
            Brushes.Black);
        return border;
    }

    private static Grid CreateEquipmentDetailTable(IReadOnlyList<LocalRentalAsset> assets, int startNumber)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.9, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.65, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < assets.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddEquipmentHeaderCell(grid, "No.", 0, 0, 2, 1);
        AddEquipmentHeaderCell(grid, "품목분류", 0, 1, 2, 1);
        AddEquipmentHeaderCell(grid, "품명", 0, 2, 2, 1);
        AddEquipmentHeaderCell(grid, "기계번호", 0, 3, 2, 1);
        AddEquipmentHeaderCell(grid, "보증금", 0, 4, 2, 1);
        AddEquipmentHeaderCell(grid, "렌탈요금", 0, 5, 2, 1);
        AddEquipmentHeaderCell(grid, "월 제한매수", 0, 6, 1, 2);
        AddEquipmentHeaderCell(grid, "추가요금", 0, 8, 1, 2);
        AddEquipmentHeaderCell(grid, "무상품목", 0, 10, 2, 1);
        AddEquipmentHeaderCell(grid, "유상품목", 0, 11, 2, 1);
        AddEquipmentHeaderCell(grid, "계약시작", 0, 12, 2, 1);
        AddEquipmentHeaderCell(grid, "설치위치", 0, 13, 2, 1);

        AddEquipmentHeaderCell(grid, "K제한", 1, 6);
        AddEquipmentHeaderCell(grid, "C제한", 1, 7);
        AddEquipmentHeaderCell(grid, "K추가", 1, 8);
        AddEquipmentHeaderCell(grid, "C추가", 1, 9);

        for (var index = 0; index < assets.Count; index++)
        {
            var row = assets[index];
            var rowIndex = index + 2;
            AddEquipmentValueCell(grid, $"{startNumber + index}", rowIndex, 0, TextAlignment.Center);
            AddEquipmentValueCell(grid, ValueOrDash(row.ItemCategoryName), rowIndex, 1);
            AddEquipmentValueCell(grid, ValueOrDash(row.ItemName), rowIndex, 2, autoShrink: true);
            AddEquipmentValueCell(grid, ValueOrDash(row.MachineNumber), rowIndex, 3, autoShrink: true);
            AddEquipmentValueCell(grid, ValueOrDash(row.DepositText), rowIndex, 4, TextAlignment.Center);
            AddEquipmentValueCell(grid, row.MonthlyFee > 0m ? $"{row.MonthlyFee:N0}" : ValueOrDash(null), rowIndex, 5, TextAlignment.Right);
            AddEquipmentValueCell(grid, "-", rowIndex, 6, TextAlignment.Center);
            AddEquipmentValueCell(grid, "-", rowIndex, 7, TextAlignment.Center);
            AddEquipmentValueCell(grid, "-", rowIndex, 8, TextAlignment.Center);
            AddEquipmentValueCell(grid, "-", rowIndex, 9, TextAlignment.Center);
            AddEquipmentValueCell(grid, ValueOrDash(row.FreeSupplyItems), rowIndex, 10);
            AddEquipmentValueCell(grid, ValueOrDash(row.PaidSupplyItems), rowIndex, 11);
            AddEquipmentValueCell(grid, ValueOrDash(FormatDate(row.ContractStartDate)), rowIndex, 12, TextAlignment.Center);
            AddEquipmentValueCell(grid, ValueOrDash(row.InstallLocation), rowIndex, 13, autoShrink: true);
        }

        return grid;
    }

    private static void AddEquipmentHeaderCell(Grid grid, string text, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        var cell = CreateEquipmentCell(text, true, TextAlignment.Center, 8.9, new Thickness(3, 4, 3, 4));
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        if (rowSpan > 1)
            Grid.SetRowSpan(cell, rowSpan);
        if (columnSpan > 1)
            Grid.SetColumnSpan(cell, columnSpan);
        grid.Children.Add(cell);
    }

    private static void AddEquipmentValueCell(Grid grid, string text, int row, int column, TextAlignment alignment = TextAlignment.Left, bool autoShrink = false)
    {
        var cell = autoShrink
            ? CreateEquipmentAutoShrinkCell(text, false, alignment, 8.5, new Thickness(3, 3, 3, 3))
            : CreateEquipmentCell(text, false, alignment, 8.7, new Thickness(3, 3, 3, 3));
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static (Grid Header, TextBlock PageNumberText) CreateContractPageHeader(string title, bool isContinuation)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = CreateText(isContinuation ? "계약 조항 계속" : "계약 개요", 11, FontWeights.SemiBold, AccentBrush);
        left.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var center = CreateText(title, isContinuation ? 21 : 24, FontWeights.Bold, Brushes.Black, TextAlignment.Center);
        Grid.SetColumn(center, 1);
        grid.Children.Add(center);

        var right = CreateText(string.Empty, 11, FontWeights.Normal, Brushes.Gray, TextAlignment.Right);
        right.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return (grid, right);
    }

    private static Border CreateContractIntroBlock(RentalContractDocumentModel model)
    {
        var intro = CreateBorder();
        intro.Background = LightAccentBrush;
        intro.Padding = new Thickness(16, 12, 16, 12);
        intro.Margin = new Thickness(0, 0, 0, 8);
        intro.Child = CreateText(model.IntroText, 12, FontWeights.SemiBold, Brushes.Black);
        return intro;
    }

    private static UIElement CreateContractHeaderTable(RentalContractDocumentModel model)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(CreateContractMetaSummary(model));
        stack.Children.Add(CreateContractPartySection(model));
        return stack;
    }

    private static Border CreateContractMetaSummary(RentalContractDocumentModel model)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(grid, "계약작성일", 0, 0, true, TextAlignment.Center);
        AddCell(grid, FormatDate(model.ContractDate), 0, 1, false, TextAlignment.Center);
        AddCell(grid, "계약개시일", 0, 2, true, TextAlignment.Center);
        AddCell(grid, FormatDate(model.ContractStartDate), 0, 3, false, TextAlignment.Center);
        AddCell(grid, "계약만료일", 0, 4, true, TextAlignment.Center);
        AddCell(grid, FormatDate(model.ContractEndDate), 0, 5, false, TextAlignment.Center);

        border.Child = grid;
        return border;
    }

    private static UIElement CreateContractPartySection(RentalContractDocumentModel model)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tenantCard = CreateContractPartyCard(
            "거래처 정보",
            model.TenantName,
            model.TenantRepresentative,
            model.TenantBusinessNumber,
            model.TenantAddress);
        Grid.SetColumn(tenantCard, 0);
        grid.Children.Add(tenantCard);

        var companyCard = CreateContractPartyCard(
            "관리업체 정보",
            model.CompanyName,
            model.CompanyRepresentative,
            model.CompanyBusinessNumber,
            model.CompanyAddress,
            model.CompanyContactNumber,
            model.CompanyFaxNumber,
            model.CompanyStampImage);
        Grid.SetColumn(companyCard, 2);
        grid.Children.Add(companyCard);

        return grid;
    }

    private static Border CreateContractPartyCard(
        string heading,
        string name,
        string representative,
        string businessNumber,
        string address,
        string? phone = null,
        string? fax = null,
        byte[]? stampImage = null)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(14, 12, 14, 12);

        var stack = new StackPanel();
        stack.Children.Add(CreateText(heading, 13, FontWeights.Bold, AccentBrush));
        stack.Children.Add(CreateContractNameRow("상호", name, stampImage));
        stack.Children.Add(CreateContractSummaryRow("대표자", representative));
        stack.Children.Add(CreateContractSummaryRow("사업자번호", businessNumber));
        stack.Children.Add(CreateContractSummaryRow("주소", address));
        if (!string.IsNullOrWhiteSpace(phone))
            stack.Children.Add(CreateContractSummaryRow("전화", phone));
        if (!string.IsNullOrWhiteSpace(fax))
            stack.Children.Add(CreateContractSummaryRow("팩스", fax));

        border.Child = stack;
        return border;
    }

    private static Grid CreateContractNameRow(string label, string value, byte[]? stampImage)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBlock = CreateText(label, 11, FontWeights.Bold, Brushes.Black);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = CreateText(Coalesce(value, "미입력"), 11, FontWeights.SemiBold, Brushes.Black);
        valueBlock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        if (stampImage is { Length: > 0 })
        {
            try
            {
                var image = new Image
                {
                    Width = 54,
                    Height = 54,
                    Margin = new Thickness(12, 0, 0, 0),
                    Source = LoadImage(stampImage),
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(image, 2);
                grid.Children.Add(image);
            }
            catch
            {
                // Ignore invalid stamp images and keep the document printable.
            }
        }

        return grid;
    }

    private static Grid CreateContractSummaryRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = CreateText(label, 11, FontWeights.Bold, Brushes.Black);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = CreateText(Coalesce(value, "미입력"), 11, FontWeights.Normal, Brushes.Black);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        return grid;
    }

    private static Border CreateContractAssetSummary(RentalContractDocumentModel model)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(14, 12, 14, 12);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(grid, "제품관리번호", 0, 0, true, TextAlignment.Center);
        AddCell(grid, model.ManagementNumber, 0, 1, false);
        AddCell(grid, "품명", 0, 2, true, TextAlignment.Center);
        AddCell(grid, model.ItemName, 0, 3, false);
        AddCell(grid, "기계번호", 1, 0, true, TextAlignment.Center);
        AddCell(grid, model.MachineNumber, 1, 1, false);
        AddCell(grid, "설치위치", 1, 2, true, TextAlignment.Center);
        AddCell(grid, model.InstallLocation, 1, 3, false);
        AddCell(grid, "보증금", 2, 0, true, TextAlignment.Center);
        AddCell(grid, model.DepositText, 2, 1, false);
        AddCell(grid, "월 렌탈요금", 2, 2, true, TextAlignment.Center);
        AddCell(grid, model.MonthlyFee == 0m ? string.Empty : $"{model.MonthlyFee:N0}원", 2, 3, false);
        AddCell(grid, "안내", 3, 0, true, TextAlignment.Center);
        AddCell(grid, "별첨 렌탈장비내역서를 기준으로 계약 장비 상세를 확인합니다.", 3, 1, false);
        Grid.SetColumnSpan(grid.Children[^1], 3);
        border.Child = grid;
        return border;
    }

    private static Border CreateClauseFragment(
        RentalContractClauseModel clause,
        IReadOnlyList<string> lines,
        bool isContinuation)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(0);
        border.Margin = new Thickness(0, 0, 0, 8);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            Background = LightAccentBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.Children.Add(CreateText(clause.Number, 12.5, FontWeights.Bold, AccentBrush));
        var titleBlock = CreateText(
            isContinuation ? $"{clause.Title} (계속)" : clause.Title,
            12.5,
            FontWeights.Bold,
            Brushes.Black);
        Grid.SetColumn(titleBlock, 1);
        headerGrid.Children.Add(titleBlock);
        header.Child = headerGrid;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var bodyPanel = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        foreach (var line in lines)
            bodyPanel.Children.Add(CreateClauseBodyLine(line));
        Grid.SetRow(bodyPanel, 1);
        root.Children.Add(bodyPanel);

        border.Child = root;
        return border;
    }

    private static int GetMaxClauseLinesForPage(
        ContractPageContext page,
        RentalContractClauseModel clause,
        IReadOnlyList<string> remainingLines,
        bool isContinuation)
    {
        var maxLines = 0;
        for (var count = 1; count <= remainingLines.Count; count++)
        {
            var fragment = CreateClauseFragment(clause, remainingLines.Take(count).ToList(), isContinuation);
            if (!CanFitOnContractPage(page, fragment))
                break;

            maxLines = count;
        }

        return maxLines;
    }

    private static Border CreateSpecialTermsFragment(IReadOnlyList<string> specialTerms, bool isContinuation)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(14, 12, 14, 12);
        border.Margin = new Thickness(0, 0, 0, 8);
        var panel = new StackPanel();
        panel.Children.Add(CreateText(isContinuation ? "【특 약 사 항 계속】" : "【특 약 사 항】", 13, FontWeights.Bold, AccentBrush));
        foreach (var term in specialTerms.Where(term => !string.IsNullOrWhiteSpace(term)))
            panel.Children.Add(CreateClauseBodyLine(term));
        border.Child = panel;
        return border;
    }

    private static int GetMaxSpecialTermsForPage(
        ContractPageContext page,
        IReadOnlyList<string> remainingTerms,
        bool isContinuation)
    {
        var maxTerms = 0;
        for (var count = 1; count <= remainingTerms.Count; count++)
        {
            var fragment = CreateSpecialTermsFragment(remainingTerms.Take(count).ToList(), isContinuation);
            if (!CanFitOnContractPage(page, fragment))
                break;

            maxTerms = count;
        }

        return maxTerms;
    }

    private static IEnumerable<string> SplitDocumentLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [string.Empty];

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static TextBlock CreateClauseBodyLine(string text)
    {
        var block = CreateText(text, 11.2, FontWeights.Normal, Brushes.Black);
        block.Margin = NeedsClauseIndent(text)
            ? new Thickness(14, 0, 0, 3)
            : new Thickness(0, 0, 0, 3);
        return block;
    }

    private static Border CreateContractClosingBlock(RentalContractDocumentModel model)
    {
        var closingBlock = CreateBorder();
        closingBlock.Padding = new Thickness(14, 12, 14, 12);
        closingBlock.Margin = new Thickness(0, 0, 0, 8);
        closingBlock.Child = new StackPanel
        {
            Children =
            {
                CreateText(model.ClosingLine1, 11.5, FontWeights.SemiBold, Brushes.Black),
                CreateText(model.ClosingLine2, 11.5, FontWeights.SemiBold, Brushes.Black)
            }
        };

        return closingBlock;
    }

    private static UIElement CreateContractExecutionSection(RentalContractDocumentModel model)
    {
        var panel = new StackPanel();

        var contractDateText = CreateText($"계약일 : {FormatDateWithKorean(model.ContractDate)}", 12, FontWeights.SemiBold, Brushes.Black, TextAlignment.Right);
        contractDateText.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(contractDateText);

        var signatureGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        signatureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        signatureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        signatureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        signatureGrid.Children.Add(CreateSignatureBlock("임차업체", model.TenantName, model.TenantRepresentative, model.TenantBusinessNumber, model.TenantAddress, null, 0));
        signatureGrid.Children.Add(CreateSignatureBlock("관리업체", model.CompanyName, model.CompanyRepresentative, model.CompanyBusinessNumber, model.CompanyAddress, model.CompanyStampImage, 2));
        panel.Children.Add(signatureGrid);

        panel.Children.Add(CreateContractFooterContactBlock(model));
        return panel;
    }

    private static bool CanFitOnContractPage(ContractPageContext page, UIElement element)
        => page.UsedHeight + MeasureContractElementHeight(page.BodyWidth, element) <= page.MaxBodyHeight;

    private static double MeasureContractElementHeight(double width, UIElement element)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        return element.DesiredSize.Height;
    }

    private static bool NeedsClauseIndent(string text)
        => text.StartsWith("(")
           || text.StartsWith("①")
           || text.StartsWith("②")
           || text.StartsWith("③")
           || text.StartsWith("④")
           || text.StartsWith("⑤")
           || text.StartsWith("1.")
           || text.StartsWith("2.")
           || text.StartsWith("3.");

    private static Border CreateContractFooterContactBlock(RentalContractDocumentModel model)
    {
        var border = CreateBorder();
        border.Padding = new Thickness(14, 12, 14, 12);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddCell(grid, model.FooterBuyerLabel, 0, 0, true, TextAlignment.Center);
        AddCell(grid, model.TenantName, 0, 1, false);
        AddCell(grid, model.FooterCompanyLabel, 0, 2, true, TextAlignment.Center);
        AddCell(grid, model.CompanyName, 0, 3, false);
        AddCell(grid, "전 화", 1, 0, true, TextAlignment.Center);
        AddCell(grid, model.CompanyContactNumber, 1, 1, false);
        AddCell(grid, "팩 스", 1, 2, true, TextAlignment.Center);
        AddCell(grid, model.CompanyFaxNumber, 1, 3, false);
        AddCell(grid, "문서명", 2, 0, true, TextAlignment.Center);
        AddCell(grid, model.Title, 2, 1, false);
        Grid.SetColumnSpan(grid.Children[^1], 3);
        border.Child = grid;
        return border;
    }

    private static UIElement CreateSignatureBlock(
        string heading,
        string companyName,
        string representative,
        string businessNumber,
        string address,
        byte[]? stampImage,
        int column)
    {
        var border = CreateBorder();
        border.Margin = new Thickness(column == 0 ? 0 : 0, 0, 0, 0);
        border.Padding = new Thickness(14, 14, 14, 14);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(CreateText(heading, 13, FontWeights.Bold, AccentBrush));
        content.Children.Add(CreateText($"상호 : {Coalesce(companyName, "미입력")}", 11.5, FontWeights.Normal, Brushes.Black));
        content.Children.Add(CreateText($"대표자 : {Coalesce(representative, "미입력")}", 11.5, FontWeights.Normal, Brushes.Black));
        content.Children.Add(CreateText($"사업자번호 : {Coalesce(businessNumber, "미입력")}", 11.5, FontWeights.Normal, Brushes.Black));
        content.Children.Add(CreateText($"주소 : {Coalesce(address, "미입력")}", 11.5, FontWeights.Normal, Brushes.Black));
        grid.Children.Add(content);

        if (stampImage is { Length: > 0 })
        {
            try
            {
                var image = new Image
                {
                    Width = 96,
                    Height = 96,
                    Margin = new Thickness(14, 0, 0, 0),
                    Source = LoadImage(stampImage),
                    Stretch = Stretch.Uniform
                };
                Grid.SetColumn(image, 1);
                grid.Children.Add(image);
            }
            catch
            {
                // Ignore invalid stamp images and keep the document printable.
            }
        }

        border.Child = grid;
        Grid.SetColumn(border, column);
        return border;
    }

    private sealed class ContractPageContext(
        FixedPage page,
        StackPanel bodyPanel,
        TextBlock pageNumberText,
        double bodyWidth,
        double maxBodyHeight)
    {
        public FixedPage Page { get; } = page;
        public StackPanel BodyPanel { get; } = bodyPanel;
        public TextBlock PageNumberText { get; } = pageNumberText;
        public double BodyWidth { get; } = bodyWidth;
        public double MaxBodyHeight { get; } = maxBodyHeight;
        public double UsedHeight { get; private set; }

        public void Add(UIElement element)
        {
            UsedHeight += MeasureContractElementHeight(BodyWidth, element);
            BodyPanel.Children.Add(element);
        }
    }

    private static BitmapImage LoadImage(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string BuildCustomerAddress(LocalCustomer? customer)
    {
        if (customer is null)
            return string.Empty;

        return string.Join(' ', new[] { customer.Address, customer.DetailAddress }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildReturnReason(LocalRentalAsset asset)
    {
        if (string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase))
            return "폐기 예정";
        if (string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase))
            return "계약 종료 또는 회수 처리";
        if (string.Equals(asset.AssetStatus, "대기", StringComparison.OrdinalIgnoreCase))
            return "재배치 또는 보류";
        return string.Empty;
    }

    private static string BuildSaleableState(LocalRentalAsset asset)
    {
        if (string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase))
            return "불가";
        if (string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase))
            return "점검 후 판단";
        return "가능";
    }

    private static string BuildContractPeriod(LocalRentalAsset asset)
    {
        if (asset.ContractStartDate is null && asset.RentalEndDate is null)
            return "미입력";

        return $"{FormatDate(asset.ContractStartDate)} ~ {FormatDate(asset.RentalEndDate)}";
    }

    private static string ResolveOfficeName(string? officeCode, LocalCompanyProfile? companyProfile)
    {
        if (!string.IsNullOrWhiteSpace(companyProfile?.TradeName))
            return companyProfile.TradeName;

        return OfficeCodeCatalog.GetOfficeDisplayName(officeCode);
    }

    private static string ResolveAssetResponsibleOfficeName(LocalRentalAsset asset)
        => OfficeCodeCatalog.GetOfficeDisplayName(
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode)
                ? asset.OfficeCode
                : asset.ResponsibleOfficeCode);

    private static string ResolveAssetManagementCompanyName(
        LocalRentalAsset asset,
        IReadOnlyDictionary<string, string>? managementCompanyNames)
    {
        var code = Coalesce(asset.ManagementCompanyCode, asset.OfficeCode, asset.ResponsibleOfficeCode);
        if (string.IsNullOrWhiteSpace(code))
            return "미입력";

        var normalizedCode = NormalizeOfficeCode(code);
        if (managementCompanyNames is not null)
        {
            if (managementCompanyNames.TryGetValue(code.Trim(), out var rawName) &&
                !string.IsNullOrWhiteSpace(rawName))
            {
                return rawName.Trim();
            }

            if (managementCompanyNames.TryGetValue(normalizedCode, out var normalizedName) &&
                !string.IsNullOrWhiteSpace(normalizedName))
            {
                return normalizedName.Trim();
            }
        }

        return OfficeCodeCatalog.GetOfficeDisplayName(normalizedCode);
    }

    private static string ResolveContractPhoneNumber(LocalCompanyProfile companyProfile)
    {
        if (!string.IsNullOrWhiteSpace(companyProfile.ContactNumber))
            return companyProfile.ContactNumber.Trim();

        return NormalizeOfficeCode(companyProfile.OfficeCode) switch
        {
            "ITWORLD" => "",
            "USENET" => "",
            _ => string.Empty
        };
    }

    private static string ResolveContractFaxNumber(LocalCompanyProfile companyProfile)
    {
        return NormalizeOfficeCode(companyProfile.OfficeCode) switch
        {
            "ITWORLD" => "",
            "USENET" => "",
            _ => string.Empty
        };
    }

    private static string NormalizeOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, OfficeCodeCatalog.Usenet);

    private static string FormatDate(DateOnly? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatDateWithKorean(DateOnly? value)
        => value?.ToString("yyyy년 M월 d일", CultureInfo.InvariantCulture) ?? "미입력";

    private static string Coalesce(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

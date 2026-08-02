param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot "..\translations")
)

$ErrorActionPreference = "Stop"

# 仅在同一条原文出现对应英文名时检查，避免把普通中文词误判为人名。
$nameRules = @(
    [pscustomobject]@{ Source = "Andre"; ChineseForms = @("安德烈") },
    [pscustomobject]@{ Source = "Aiden"; ChineseForms = @("艾登") },
    [pscustomobject]@{ Source = "Goro"; ChineseForms = @("五郎", "吾郎") },
    [pscustomobject]@{ Source = "Yoshinori"; ChineseForms = @("吉野") },
    [pscustomobject]@{ Source = "Yoshi"; ChineseForms = @("吉野") },
    [pscustomobject]@{ Source = "Yuri"; ChineseForms = @("尤里") },
    [pscustomobject]@{ Source = "Lloyd"; ChineseForms = @("劳埃德", "勞埃德") },
    [pscustomobject]@{ Source = "Darius"; ChineseForms = @("达里乌斯", "達里烏斯") },
    [pscustomobject]@{ Source = "Dar"; ChineseForms = @("达尔", "達爾") },
    [pscustomobject]@{ Source = "Hyunjin"; ChineseForms = @("贤真", "玄真", "賢真", "玄眞") },
    [pscustomobject]@{ Source = "Jin"; ChineseForms = @("贤真", "玄真", "賢真", "玄眞") },
    [pscustomobject]@{ Source = "Emilia"; ChineseForms = @("埃米莉亚", "埃米莉婭", "艾米莉亚", "艾米莉婭") },
    [pscustomobject]@{ Source = "Yoichi"; ChineseForms = @("洋一") },
    [pscustomobject]@{ Source = "Taiga"; ChineseForms = @("大河") },
    [pscustomobject]@{ Source = "Keitaro"; ChineseForms = @("启太郎", "啟太郎", "圭太郎", "启太朗", "啟太朗", "圭太朗") },
    [pscustomobject]@{ Source = "Hunter"; ChineseForms = @("亨特") },
    [pscustomobject]@{ Source = "Hiro"; ChineseForms = @("弘", "浩") },
    [pscustomobject]@{ Source = "Natsumi"; ChineseForms = @("夏美") },
    [pscustomobject]@{ Source = "Naoto"; ChineseForms = @("直人") },
    [pscustomobject]@{ Source = "Reimond"; ChineseForms = @("雷蒙德") },
    [pscustomobject]@{ Source = "Justin"; ChineseForms = @("贾斯汀", "賈斯汀") },
    [pscustomobject]@{ Source = "Vera"; ChineseForms = @("维拉", "維拉") },
    [pscustomobject]@{ Source = "William"; ChineseForms = @("威廉") },
    [pscustomobject]@{ Source = "Clermont"; ChineseForms = @("克莱蒙", "克萊蒙") },
    [pscustomobject]@{ Source = "Nomoru"; ChineseForms = @("野室", "野村", "野邨") },
    [pscustomobject]@{ Source = "Nagira"; ChineseForms = @("名良", "名罗", "名羅") },
    [pscustomobject]@{ Source = "Sirius"; ChineseForms = @("西里乌斯", "西里厄斯", "西里烏斯", "西里厄斯") },
    [pscustomobject]@{ Source = "Najjar"; ChineseForms = @("纳贾尔", "納賈爾") },
    [pscustomobject]@{ Source = "Flynn"; ChineseForms = @("弗林") },
    [pscustomobject]@{ Source = "Komarova"; ChineseForms = @("科马洛娃", "科馬洛娃") },
    [pscustomobject]@{ Source = "Choi"; ChineseForms = @("崔") },
    [pscustomobject]@{ Source = "Nagame"; ChineseForms = @("长目", "長目", "长雨", "長雨") },
    [pscustomobject]@{ Source = "Akatora"; ChineseForms = @("赤虎") },
    [pscustomobject]@{ Source = "Eduard"; ChineseForms = @("爱德华", "愛德華") },
    [pscustomobject]@{ Source = "Lee"; ChineseForms = @("李") },
    [pscustomobject]@{ Source = "Felix"; ChineseForms = @("菲利克斯", "費利克斯") },
    [pscustomobject]@{ Source = "Seto"; ChineseForms = @("濑户", "瀨戶") },
    [pscustomobject]@{ Source = "Dynamite"; ChineseForms = @("炸药", "炸藥") },
    [pscustomobject]@{ Source = "Mr. Perfect"; ChineseForms = @("完美先生") },
    [pscustomobject]@{ Source = "Twinkerbell"; ChineseForms = @("小仙男", "闪亮仙子", "閃亮仙子") },
    [pscustomobject]@{ Source = "Torch-head"; SourceVariants = @("TORCH-HEAD"); ChineseForms = @("火炬头", "火炬頭") },
    [pscustomobject]@{ Source = "Sourpuss"; ChineseForms = @("苦瓜脸", "苦瓜臉") },
    [pscustomobject]@{ Source = "Smooth-Talker"; ChineseForms = @("花言巧语", "花言巧語") },
    [pscustomobject]@{ Source = "Wolfboy"; ChineseForms = @("狼小子") },
    [pscustomobject]@{ Source = "Pipsqueak"; ChineseForms = @("小不点", "小不點") },
    [pscustomobject]@{ Source = "Willy Wanker"; ChineseForms = @("威利自慰家") },
    [pscustomobject]@{ Source = "Sheriff Brokeback"; ChineseForms = @("断背山警长", "斷背山警長", "断背警长", "斷背警長") },
    [pscustomobject]@{ Source = "Frogboy"; ChineseForms = @("青蛙小子") },
    [pscustomobject]@{ Source = "Geezer"; ChineseForms = @("老头", "老頭") },
    [pscustomobject]@{ Source = "Buttcheeks"; SourceVariants = @("BUTTCHEEKS"); ChineseForms = @("屁股蛋") },
    [pscustomobject]@{ Source = "Mr. Future President"; ChineseForms = @("未来主席", "未來主席") },
    [pscustomobject]@{ Source = "Gramps"; ChineseForms = @("老爷子", "老爺子", "爷爷", "爺爺") }
)

# 全名必须保持原有顺序，不能仅保留英文但倒置姓名。
$nameOrderRules = @(
    [pscustomobject]@{ Source = "Yoshinori Nagira"; InvalidForm = "NagiraYoshinori" },
    [pscustomobject]@{ Source = "Yuri Nomoru"; InvalidForm = "NomoruYuri" }
)

function Get-RenPyMarkers([string]$text) {
    return @(
        [regex]::Matches($text, "\{[^{}]*\}|\[[^\[\]]+\]") |
            ForEach-Object { $_.Value }
    )
}

if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
    throw "未找到分卷目录：$SourceDirectory"
}

$translationFiles = @(
    Get-ChildItem -LiteralPath $SourceDirectory -Filter "*.jsonl" -File |
        Sort-Object Name
)
if ($translationFiles.Count -eq 0) {
    throw "未找到 JSONL 分卷：$SourceDirectory"
}

$seenSources = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal
)
$issues = [System.Collections.Generic.List[string]]::new()
$recordCount = 0
$jsonErrorCount = 0
$duplicateCount = 0
$markerErrorCount = 0
$nameErrorCount = 0
$nameOrderErrorCount = 0

foreach ($translationFile in $translationFiles) {
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($translationFile.FullName)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $record = $line | ConvertFrom-Json -ErrorAction Stop
            $source = [string]$record.source
            $translation = [string]$record.translation
        } catch {
            $jsonErrorCount++
            $issues.Add("JSON：$($translationFile.Name):$lineNumber")
            continue
        }

        $recordCount++
        if ([string]::IsNullOrWhiteSpace($source) -or [string]::IsNullOrWhiteSpace($translation)) {
            $issues.Add("空字段：$($translationFile.Name):$lineNumber")
        }
        if (-not $seenSources.Add($source)) {
            $duplicateCount++
            $issues.Add("重复原文：$($translationFile.Name):$lineNumber")
        }

        $sourceMarkers = Get-RenPyMarkers $source
        $translationMarkers = Get-RenPyMarkers $translation
        if (($sourceMarkers -join [char]0) -cne ($translationMarkers -join [char]0)) {
            $markerErrorCount++
            $issues.Add("Ren'Py 标记：$($translationFile.Name):$lineNumber")
        }

        foreach ($rule in $nameRules) {
            $sourceForms = @($rule.Source)
            $sourceVariantsProperty = $rule.PSObject.Properties["SourceVariants"]
            if ($null -ne $sourceVariantsProperty) {
                $sourceForms += @($sourceVariantsProperty.Value)
            }
            $sourceHasName = $false
            foreach ($sourceForm in $sourceForms) {
                $sourcePattern = "(?<![A-Za-z])" + [regex]::Escape($sourceForm) + "(?![A-Za-z])"
                if ($source -cmatch $sourcePattern) {
                    $sourceHasName = $true
                    break
                }
            }
            if (-not $sourceHasName) {
                continue
            }
            $remainingForms = @(
                $rule.ChineseForms | Where-Object { $translation.Contains($_) }
            )
            if ($remainingForms.Count -gt 0) {
                $nameErrorCount++
                $issues.Add(
                    "中文人名：$($translationFile.Name):$lineNumber [$($remainingForms -join ',')]"
                )
            }
        }

        foreach ($rule in $nameOrderRules) {
            $sourcePattern = "(?<![A-Za-z])" + [regex]::Escape($rule.Source) + "(?![A-Za-z])"
            if ($source -cmatch $sourcePattern -and $translation.Contains($rule.InvalidForm)) {
                $nameOrderErrorCount++
                $issues.Add("姓名顺序：$($translationFile.Name):$lineNumber [$($rule.InvalidForm)]")
            }
        }
    }
}

Write-Host (
    "已校验 {0} 个分卷、{1} 条：JSON={2}，重复={3}，标记={4}，中文人名={5}，姓名顺序={6}" -f
    $translationFiles.Count,
    $recordCount,
    $jsonErrorCount,
    $duplicateCount,
    $markerErrorCount,
    $nameErrorCount,
    $nameOrderErrorCount
)

if ($issues.Count -gt 0) {
    $issues | Select-Object -First 100 | Write-Host
    throw "翻译人名校验失败：共 $($issues.Count) 项"
}

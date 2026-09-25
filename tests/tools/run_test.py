"""
Seeing.Agent WebUI Playwright 测试脚本
"""
import json
import time
from datetime import datetime
from playwright.sync_api import sync_playwright

BASE_URL = "http://localhost:5000"
TIMEOUT = 30000

test_results = {
    "test_time": datetime.now().isoformat(),
    "base_url": BASE_URL,
    "modules": [],
    "summary": {"total": 0, "passed": 0, "failed": 0, "blocked": 0}
}

def log_result(module, test_id, test_name, status, duration=0, message=""):
    result = {"test_id": test_id, "test_name": test_name, "status": status, "duration_ms": duration, "message": message}
    
    module_found = None
    for m in test_results["modules"]:
        if m["module_name"] == module:
            module_found = m
            break
    
    if not module_found:
        module_found = {"module_name": module, "tests": [], "passed": 0, "failed": 0, "blocked": 0}
        test_results["modules"].append(module_found)
    
    module_found["tests"].append(result)
    test_results["summary"]["total"] += 1
    
    if status == "PASS":
        module_found["passed"] += 1
        test_results["summary"]["passed"] += 1
    elif status == "FAIL":
        module_found["failed"] += 1
        test_results["summary"]["failed"] += 1
    else:
        module_found["blocked"] += 1
        test_results["summary"]["blocked"] += 1

def run_tests():
    output_lines = []
    output_lines.append("=" * 60)
    output_lines.append("Seeing.Agent WebUI 自动化测试报告")
    output_lines.append("=" * 60)
    output_lines.append(f"测试时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    output_lines.append(f"基础URL: {BASE_URL}")
    
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        context = browser.new_context(viewport={"width": 1920, "height": 1080}, locale="zh-CN")
        page = context.new_page()
        page.set_default_timeout(TIMEOUT)
        
        try:
            # 测试首页
            output_lines.append("\n[模块1: 首页]")
            start = time.time()
            try:
                page.goto(BASE_URL)
                page.wait_for_load_state("networkidle")
                hero = page.query_selector(".home-hero")
                if hero:
                    log_result("首页", "TC-HOME-001", "页面加载", "PASS", int((time.time()-start)*1000))
                else:
                    log_result("首页", "TC-HOME-001", "页面加载", "FAIL", int((time.time()-start)*1000), "Hero区域未找到")
            except Exception as e:
                log_result("首页", "TC-HOME-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            try:
                stats = page.query_selector_all(".stat-card")
                log_result("首页", "TC-HOME-002", "统计卡片", "PASS" if len(stats) >= 3 else "FAIL", int((time.time()-start)*1000), f"数量: {len(stats)}")
            except Exception as e:
                log_result("首页", "TC-HOME-002", "统计卡片", "FAIL", 0, str(e))
            
            # 测试会话管理
            output_lines.append("[模块2: 会话管理]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/sessions")
                page.wait_for_load_state("networkidle")
                log_result("会话管理", "TC-SESSIONS-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("会话管理", "TC-SESSIONS-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试工具管理
            output_lines.append("[模块3: 工具管理]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/tools")
                page.wait_for_load_state("networkidle")
                tools = page.query_selector_all(".item-card")
                log_result("工具管理", "TC-TOOLS-001", "工具列表", "PASS" if len(tools) > 0 else "FAIL", int((time.time()-start)*1000), f"工具数: {len(tools)}")
            except Exception as e:
                log_result("工具管理", "TC-TOOLS-001", "工具列表", "FAIL", 0, str(e))
            
            # 测试技能管理
            output_lines.append("[模块4: 技能管理]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/skills")
                page.wait_for_load_state("networkidle")
                log_result("技能管理", "TC-SKILLS-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("技能管理", "TC-SKILLS-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试MCP
            output_lines.append("[模块5: MCP管理]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/mcp")
                page.wait_for_load_state("networkidle")
                log_result("MCP管理", "TC-MCP-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("MCP管理", "TC-MCP-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试智能体
            output_lines.append("[模块6: 智能体管理]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/agents")
                page.wait_for_load_state("networkidle")
                log_result("智能体管理", "TC-AGENTS-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("智能体管理", "TC-AGENTS-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试模型
            output_lines.append("[模块7: 模型管理]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/models")
                page.wait_for_load_state("networkidle")
                log_result("模型管理", "TC-MODELS-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("模型管理", "TC-MODELS-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试Gateway
            output_lines.append("[模块8: Gateway]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/gateway")
                page.wait_for_load_state("networkidle")
                log_result("Gateway", "TC-GATEWAY-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("Gateway", "TC-GATEWAY-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试定时任务
            output_lines.append("[模块9: 定时任务]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/cron-jobs")
                page.wait_for_load_state("networkidle")
                log_result("定时任务", "TC-CRON-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("定时任务", "TC-CRON-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试心跳
            output_lines.append("[模块10: 心跳]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/heartbeat")
                page.wait_for_load_state("networkidle")
                log_result("心跳", "TC-HB-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("心跳", "TC-HB-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试记忆
            output_lines.append("[模块11: 记忆]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/memory")
                page.wait_for_load_state("networkidle")
                log_result("记忆", "TC-MEM-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("记忆", "TC-MEM-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试ACP
            output_lines.append("[模块12: ACP]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/acp")
                page.wait_for_load_state("networkidle")
                log_result("ACP", "TC-ACP-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("ACP", "TC-ACP-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试安全
            output_lines.append("[模块13: 安全]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/security")
                page.wait_for_load_state("networkidle")
                log_result("安全", "TC-SEC-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("安全", "TC-SEC-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试系统设置
            output_lines.append("[模块14: 系统设置]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/settings")
                page.wait_for_load_state("networkidle")
                log_result("系统设置", "TC-SET-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("系统设置", "TC-SET-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 测试Gateway客户端
            output_lines.append("[模块15: Gateway客户端]")
            start = time.time()
            try:
                page.goto(f"{BASE_URL}/gateway-clients")
                page.wait_for_load_state("networkidle")
                log_result("Gateway客户端", "TC-CLIENTS-001", "页面加载", "PASS", int((time.time()-start)*1000))
            except Exception as e:
                log_result("Gateway客户端", "TC-CLIENTS-001", "页面加载", "FAIL", int((time.time()-start)*1000), str(e))
            
            # 保存截图
            page.screenshot(path="E:/Projects/CSharp/Seeing.Agent/tests/screenshot.png", full_page=True)
            
        except Exception as e:
            output_lines.append(f"ERROR: {str(e)}")
        
        finally:
            browser.close()
    
    # 输出摘要
    output_lines.append("\n" + "=" * 60)
    output_lines.append("测试摘要")
    output_lines.append("=" * 60)
    output_lines.append(f"总测试数: {test_results['summary']['total']}")
    output_lines.append(f"通过: {test_results['summary']['passed']}")
    output_lines.append(f"失败: {test_results['summary']['failed']}")
    output_lines.append(f"阻塞: {test_results['summary']['blocked']}")
    if test_results['summary']['total'] > 0:
        output_lines.append(f"通过率: {test_results['summary']['passed']/test_results['summary']['total']*100:.1f}%")
    
    # 保存结果
    with open("E:/Projects/CSharp/Seeing.Agent/tests/test_results.json", "w", encoding="utf-8") as f:
        json.dump(test_results, f, ensure_ascii=False, indent=2)
    
    with open("E:/Projects/CSharp/Seeing.Agent/tests/test_output.txt", "w", encoding="utf-8") as f:
        f.write("\n".join(output_lines))
    
    return test_results

if __name__ == "__main__":
    run_tests()
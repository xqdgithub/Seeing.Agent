"""验证定时任务表单验证修复"""
import time
from playwright.sync_api import sync_playwright

def test_form_validation():
    print("Testing cron job form validation...")
    results = []
    
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        context = browser.new_context(viewport={"width": 1920, "height": 1080})
        page = context.new_page()
        
        try:
            # 进入定时任务页面
            page.goto("http://localhost:5000/cron-jobs")
            page.wait_for_load_state("networkidle")
            
            # 点击新建任务按钮
            new_btn = page.query_selector('button:has-text("新建任务")')
            if not new_btn:
                print("ERROR: Cannot find 'New Job' button")
                return []
            
            new_btn.click()
            page.wait_for_timeout(500)
            
            # 检查弹窗是否打开
            modal = page.query_selector('.ant-modal-content')
            if not modal:
                print("ERROR: Modal not opened")
                return []
            
            print("Modal opened, testing validation...")
            
            # 测试1: 直接点击确定，验证是否阻止提交
            print("\n[Test 1] Testing empty form submission...")
            confirm_btn = modal.query_selector('button:has-text("确定")')
            if confirm_btn:
                confirm_btn.click()
                page.wait_for_timeout(500)
                
                # 检查是否有错误提示
                error_alert = page.query_selector('.ant-alert-error, .ant-modal-content .ant-alert')
                if error_alert:
                    error_text = error_alert.inner_text()
                    print(f"  PASS: Validation error shown: {error_text[:50]}...")
                    results.append(("Empty form validation", "PASS", error_text[:50]))
                else:
                    print("  FAIL: No validation error shown")
                    results.append(("Empty form validation", "FAIL", "No error shown"))
            else:
                print("  ERROR: Cannot find confirm button")
            
            # 测试2: 验证任务ID为空
            print("\n[Test 2] Testing empty task ID...")
            # 任务ID输入框应该已有错误状态或再次点击确定会有提示
            
            # 测试3: 填写无效的 Cron 表达式
            print("\n[Test 3] Testing invalid cron expression...")
            
            # 选择 Cron 类型
            schedule_type = modal.query_selector('.ant-select-selector')
            if schedule_type:
                schedule_type.click()
                page.wait_for_timeout(300)
                cron_option = page.query_selector('.ant-select-dropdown .ant-select-item:has-text("Cron")')
                if cron_option:
                    cron_option.click()
                    page.wait_for_timeout(300)
            
            # 填写无效 Cron
            cron_input = modal.query_selector('input[placeholder*="0 9"]')
            if cron_input:
                cron_input.fill("invalid cron")
                confirm_btn = modal.query_selector('button:has-text("确定")')
                if confirm_btn:
                    confirm_btn.click()
                    page.wait_for_timeout(500)
                    
                    error_alert = page.query_selector('.ant-alert-error')
                    if error_alert:
                        error_text = error_alert.inner_text()
                        if "Cron" in error_text or "cron" in error_text.lower():
                            print(f"  PASS: Cron validation error shown")
                            results.append(("Cron validation", "PASS", error_text[:50]))
                        else:
                            print(f"  PARTIAL: Error shown but not Cron specific: {error_text[:50]}")
                            results.append(("Cron validation", "PARTIAL", error_text[:50]))
                    else:
                        print("  FAIL: No cron validation error")
                        results.append(("Cron validation", "FAIL", "No error"))
            
            # 测试4: 测试必填项提示
            print("\n[Test 4] Testing required field hints...")
            required_markers = modal.query_selector_all('.ant-form-item-required')
            print(f"  Found {len(required_markers)} required field markers")
            results.append(("Required markers", "PASS" if len(required_markers) >= 3 else "FAIL", f"Found {len(required_markers)}"))
            
            # 关闭弹窗
            cancel_btn = modal.query_selector('button:has-text("取消")')
            if cancel_btn:
                cancel_btn.click()
                page.wait_for_timeout(300)
            
        except Exception as e:
            print(f"Error: {e}")
            results.append(("Test execution", "ERROR", str(e)))
        finally:
            browser.close()
    
    return results

if __name__ == "__main__":
    results = test_form_validation()
    
    print("\n" + "="*50)
    print("Test Results Summary")
    print("="*50)
    
    passed = sum(1 for r in results if r[1] == "PASS")
    failed = sum(1 for r in results if r[1] in ["FAIL", "ERROR"])
    
    for test_name, status, detail in results:
        icon = "PASS" if status == "PASS" else "FAIL"
        print(f"[{icon}] {test_name}: {detail}")
    
    print(f"\nTotal: {len(results)}, Passed: {passed}, Failed: {failed}")
    print(f"Validation is {'WORKING' if passed > 0 else 'NOT WORKING'}")
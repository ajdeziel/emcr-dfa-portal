import { Component, Input, OnInit } from '@angular/core';
import { AbstractControl, FormBuilder, FormControl, FormGroup, FormGroupDirective, NgForm, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { StepCreateProfileService } from '../../step-create-profile/step-create-profile.service';
import * as globalConst from '../../../../core/services/global-constants';
import { MatCheckboxChange } from '@angular/material/checkbox';
import { ErrorStateMatcher } from '@angular/material/core';

import { SecurityQuestions } from 'src/app/core/models/profile';
import { CustomValidationService } from 'src/app/core/services/customValidation.service';

@Component({
  selector: 'app-security-questions',
  templateUrl: './security-questions.component.html',
  styleUrls: ['./security-questions.component.scss']
})
export class SecurityQuestionsComponent implements OnInit {
  questionForm: FormGroup = null;
  secQuestions: string[];

  bypassQuestions = false;

  constructor(
    private router: Router,
    private stepCreateProfileService: StepCreateProfileService,
    private formBuilder: FormBuilder,
    private customValidationService: CustomValidationService
  ) {}

  ngOnInit(): void {
    this.secQuestions = [
      'What was the name of your first pet?',
      'What was your first car’s make and model? (e.g. Ford Taurus)',
      'Where was your first job?',
      'What is your favourite children\'s book?',
      'In what city or town was your mother born?',
      'What is your favourite movie?',
      'What is your oldest sibling\'s middle name?',
      'What month and day is your anniversary?',
      'What was your childhood nickname?',
      'What were the last four digits of your childhood telephone number?',
      'In what town or city did you meet your spouse or partner?'
    ];

    this.createQuestionForm();
  }
  
  createQuestionForm(): void {
    this.questionForm = this.formBuilder.group({
      question1: [
        this.stepCreateProfileService.securityQuestions?.question1 ?? '',
        [Validators.required]
      ],
      answer1: [
        this.stepCreateProfileService.securityQuestions?.answer1 ?? '',
        [
          Validators.required, Validators.minLength(3), Validators.maxLength(50), 
          Validators.pattern(/^[a-zA-Z0-9 ]+$/)
        ]
      ],
      question2: [
        this.stepCreateProfileService.securityQuestions?.question2 ?? '',
        [Validators.required]
      ],
      answer2: [
        this.stepCreateProfileService.securityQuestions?.answer2 ?? '',
        [
          Validators.required, Validators.minLength(3), Validators.maxLength(50), 
          Validators.pattern(/^[a-zA-Z0-9 ]+$/)
        ]
      ],
      question3: [
        this.stepCreateProfileService.securityQuestions?.question3 ?? '',
        [Validators.required]
      ],
      answer3: [
        this.stepCreateProfileService.securityQuestions?.answer3 ?? '',
        [
          Validators.required, Validators.minLength(3), Validators.maxLength(50), 
          Validators.pattern(/^[a-zA-Z0-9 ]+$/)
        ]
      ]
    },
    {
      validators: [this.customValidationService.uniqueValueValidator(["question1", "question2", "question3"])]
    });
  }
  
  get questionFormControl(): { [key: string]: AbstractControl; } {
    return this.questionForm.controls;
  }

  changeBypass(event:MatCheckboxChange) {
    this.bypassQuestions = event.checked;

    if (this.bypassQuestions) {
      // Reset dropdowns/inputs
      this.questionForm.disable();
      this.questionForm.reset();
    }
    else {
      this.questionForm.enable();
    }

    this.updateTabStatus();
  }

  next(): void {
    this.updateTabStatus();

    if (this.stepCreateProfileService.checkTabsStatus()) {
      this.stepCreateProfileService.openModal(globalConst.wizardProfileMessage);
    } else {
      this.router.navigate(['/ess-wizard/create-evacuee-profile/review']);
    }
  }

  back(): void {
    this.updateTabStatus();
    /*
    this.router.navigate([
      '/ess-wizard/create-evacuee-profile/contact'
    ]);
    */
  }
  
  updateTabStatus() {
    this.questionForm.updateValueAndValidity();

    let curSecurityQuestions: SecurityQuestions = {
      question1: this.questionForm.get('question1').value?.trim(),
      question2: this.questionForm.get('question2').value?.trim(),
      question3: this.questionForm.get('question3').value?.trim(),
  
      answer1: this.questionForm.get('answer1').value?.trim(),
      answer2: this.questionForm.get('answer2').value?.trim(),
      answer3: this.questionForm.get('answer3').value?.trim()
    }

    if (this.questionForm.valid || this.bypassQuestions) {
      this.stepCreateProfileService.setTabStatus('security-questions', 'complete');
    }
    else if (this.anyValueSet(curSecurityQuestions)) {
      this.stepCreateProfileService.setTabStatus('security-questions', 'incomplete');
    }
    else {
      this.stepCreateProfileService.setTabStatus('security-questions', 'not-started');
    }

    this.stepCreateProfileService.securityQuestions = curSecurityQuestions;
  }

  anyValueSet(sec: SecurityQuestions) {
    return (
      sec.question1 ||
      sec.question2 || 
      sec.question3 ||
      sec.answer1 ||
      sec.answer2 ||
      sec.answer3
    );
  }
}